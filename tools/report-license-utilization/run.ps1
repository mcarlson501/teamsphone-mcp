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

$script:PhoneSystemFeatures = @('phonesystem', 'mcovoiceconf', 'mcoev')
$script:CallingPlanFeatures = @('domesticcalling', 'internationalcalling', 'domesticandinternationalcalling', 'mcopstn')

function Test-FeatureMatch {
    param([string[]]$FeatureTypes, [string[]]$Patterns)
    foreach ($feature in $FeatureTypes) {
        $normalized = ($feature -replace '[\s_\-]', '').ToLowerInvariant()
        foreach ($pattern in $Patterns) { if ($normalized -like "$pattern*") { return $true } }
    }
    return $false
}

$null = Get-StageInput -InputJson $InputJson
switch ($Stage) {
    'execute' {
        $users = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -ResultSize ([int]::MaxValue) -ErrorAction Stop })
        $licensed = @()
        $callingPlanCount = 0
        foreach ($user in $users) {
            $features = @(Get-PropertyValue -InputObject $user -Name 'FeatureTypes' -Default @() | ForEach-Object { [string]$_ })
            if (-not (Test-FeatureMatch -FeatureTypes $features -Patterns $script:PhoneSystemFeatures)) { continue }
            $hasCallingPlan = Test-FeatureMatch -FeatureTypes $features -Patterns $script:CallingPlanFeatures
            if ($hasCallingPlan) { $callingPlanCount++ }
            $licensed += [ordered]@{
                userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName')
                enterpriseVoiceEnabled = [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)
                callingPlanLicensed = $hasCallingPlan
            }
        }
        $wasted = @($licensed | Where-Object { -not $_.enterpriseVoiceEnabled })
        $findings = @()
        if ($wasted.Count -gt 0) {
            $findings += [ordered]@{
                severity = 'warning'
                code = 'licensedUsersNotVoiceEnabled'
                what = "$($wasted.Count) Phone System licensed user(s) are not enabled for enterprise voice."
                why = 'These assignments consume licenses without providing PSTN calling.'
                fix = 'Review the users and either use onboard-voice-user or remove licenses that are no longer needed.'
            }
        }
        $after = [ordered]@{
            observedUserCount = $users.Count
            phoneSystemAssigned = $licensed.Count
            callingPlanAssigned = $callingPlanCount
            availablePhoneSystemLicenses = $null
            availabilityNote = 'Tenant SKU capacity requires Microsoft Graph organization licensing permission and is not exposed by the Teams-only session.'
            wastedCount = $wasted.Count
            wastedAssignments = @($wasted | Sort-Object -Property { $_.userPrincipalName })
            findings = $findings
        }
        return (Write-StageResult -Summary "Observed $($licensed.Count) Phone System assignment(s); $($wasted.Count) are not voice-enabled." -After $after)
    }
    default { throw "Tool 'report-license-utilization' does not implement stage '$Stage'." }
}