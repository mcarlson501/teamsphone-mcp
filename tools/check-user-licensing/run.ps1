param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("snapshot", "preflight", "dryrun", "execute", "verify", "rollback")]
    [string]$Stage,

    [Parameter(Mandatory = $true)]
    [string]$InputJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

# Licensing state is derived from the feature types the Teams service reports for
# the user, which avoids a second Graph credential path for a tier-0 read.
$script:PhoneSystemFeatures = @('phonesystem', 'mcovoiceconf', 'mcoev')
$script:CallingPlanFeatures = @('domesticcalling', 'internationalcalling', 'domesticandinternationalcalling', 'mcopstn')

function Test-FeatureMatch {
    param(
        [string[]]$FeatureTypes,
        [string[]]$Patterns
    )

    foreach ($feature in $FeatureTypes) {
        $normalized = ($feature -replace '[\s_\-]', '').ToLowerInvariant()
        foreach ($pattern in $Patterns) {
            if ($normalized -like "$pattern*") { return $true }
        }
    }

    return $false
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $userUpn -ErrorAction Stop }

        $featureTypes = @(Get-PropertyValue -InputObject $user -Name 'FeatureTypes' -Default @() | ForEach-Object { [string]$_ })
        $assignedPlans = @()
        foreach ($plan in @(Get-PropertyValue -InputObject $user -Name 'AssignedPlan' -Default @())) {
            $capability = Get-PropertyValue -InputObject $plan -Name 'Capability'
            if ($null -eq $capability) { $capability = Get-PropertyValue -InputObject $plan -Name 'ServicePlanName' }
            if ($null -eq $capability) { continue }

            $assignedPlans += [ordered]@{
                capability     = [string]$capability
                assignedAtUtc  = [string](Get-PropertyValue -InputObject $plan -Name 'AssignedTimestamp')
                capabilityStatus = [string](Get-PropertyValue -InputObject $plan -Name 'CapabilityStatus')
            }
        }

        $usageLocation = [string](Get-PropertyValue -InputObject $user -Name 'UsageLocation')
        $enterpriseVoiceEnabled = [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)
        $lineUri = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
        $phoneSystem = Test-FeatureMatch -FeatureTypes $featureTypes -Patterns $script:PhoneSystemFeatures
        $callingPlan = Test-FeatureMatch -FeatureTypes $featureTypes -Patterns $script:CallingPlanFeatures

        $blockers = @()
        if (-not $phoneSystem) { $blockers += 'noPhoneSystemLicense' }
        if ([string]::IsNullOrWhiteSpace($usageLocation)) { $blockers += 'noUsageLocation' }
        if (-not $enterpriseVoiceEnabled) { $blockers += 'enterpriseVoiceDisabled' }
        if ($null -eq $lineUri) { $blockers += 'noPhoneNumberAssigned' }

        $after = [ordered]@{
            userPrincipalName      = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $userUpn)
            accountEnabled         = Get-PropertyValue -InputObject $user -Name 'AccountEnabled'
            usageLocation          = if ([string]::IsNullOrWhiteSpace($usageLocation)) { $null } else { $usageLocation }
            enterpriseVoiceEnabled = $enterpriseVoiceEnabled
            lineUri                = $lineUri
            phoneSystemLicensed    = $phoneSystem
            callingPlanLicensed    = $callingPlan
            voiceReady             = $blockers.Count -eq 0
            featureTypes           = $featureTypes
            assignedPlans          = $assignedPlans
            blockers               = $blockers
        }

        $summary = if ($after.voiceReady) {
            "User $userUpn is voice-ready (Phone System licensed, number assigned)."
        }
        else {
            "User $userUpn is not voice-ready: $($blockers -join ', ')."
        }

        return (Write-StageResult -Summary $summary -After $after)
    }
    default {
        throw "Tool 'check-user-licensing' does not implement stage '$Stage'."
    }
}
