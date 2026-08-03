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

function Test-PhoneSystemLicense {
    param([string[]]$FeatureTypes)

    foreach ($feature in $FeatureTypes) {
        $normalized = ($feature -replace '[\s_\-]', '').ToLowerInvariant()
        foreach ($pattern in $script:PhoneSystemFeatures) {
            if ($normalized -like "$pattern*") { return $true }
        }
    }

    return $false
}

function Add-DiagnosticFinding {
    param(
        [System.Collections.ArrayList]$Findings,
        [string]$Severity,
        [string]$Code,
        [string]$What,
        [string]$Why,
        [string]$Fix
    )

    [void]$Findings.Add([ordered]@{
        severity = $Severity
        code     = $Code
        what     = $What
        why      = $Why
        fix      = $Fix
    })
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')

switch ($Stage) {
    'execute' {
        $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $userUpn -ErrorAction Stop }

        $featureTypes = @(Get-PropertyValue -InputObject $user -Name 'FeatureTypes' -Default @() | ForEach-Object { [string]$_ })
        $phoneSystemLicensed = Test-PhoneSystemLicense -FeatureTypes $featureTypes
        $enterpriseVoiceEnabled = [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)
        $lineUri = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
        $voiceRoutingPolicy = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'OnlineVoiceRoutingPolicy')
        $dialPlan = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'TenantDialPlan')
        $locationId = $null
        if ($null -ne $lineUri) {
            $assignments = @(Invoke-WithRetry -ScriptBlock {
                Get-CsPhoneNumberAssignment -TelephoneNumber $lineUri -ErrorAction Stop
            } | Where-Object { $null -ne $_ })
            if ($assignments.Count -gt 0) {
                $locationId = [string](Get-PropertyValue -InputObject $assignments[0] -Name 'LocationId')
            }
        }

        $findings = [System.Collections.ArrayList]::new()
        if (-not $phoneSystemLicensed) {
            Add-DiagnosticFinding -Findings $findings -Severity 'critical' -Code 'noPhoneSystemLicense' `
                -What 'The user does not have a Phone System entitlement.' `
                -Why 'Teams cannot enable PSTN calling for a user without Phone System.' `
                -Fix 'Assign a Teams Phone license in the Microsoft 365 admin center, then rerun check-user-licensing.'
        }
        if (-not $enterpriseVoiceEnabled) {
            Add-DiagnosticFinding -Findings $findings -Severity 'critical' -Code 'enterpriseVoiceDisabled' `
                -What 'Enterprise voice is disabled for the user.' `
                -Why 'The user cannot place or receive PSTN calls while enterprise voice is disabled.' `
                -Fix 'Use onboard-voice-user after licensing prerequisites are satisfied.'
        }
        if ($null -eq $lineUri) {
            Add-DiagnosticFinding -Findings $findings -Severity 'critical' -Code 'noPhoneNumberAssigned' `
                -What 'The user has no assigned phone number.' `
                -Why 'Inbound and outbound PSTN calling requires a number assignment.' `
                -Fix 'Use assign-phone-number to assign an available number.'
        }
        if ([string]::IsNullOrWhiteSpace([string]$voiceRoutingPolicy)) {
            Add-DiagnosticFinding -Findings $findings -Severity 'warning' -Code 'noVoiceRoutingPolicy' `
                -What 'The user has no explicit online voice routing policy.' `
                -Why 'Direct Routing calls may have no permitted PSTN route; Calling Plan and Operator Connect users may rely on tenant defaults.' `
                -Fix 'Verify the tenant default or use update-user-calling-policies to assign the intended voice routing policy.'
        }
        if ([string]::IsNullOrWhiteSpace([string]$dialPlan)) {
            Add-DiagnosticFinding -Findings $findings -Severity 'warning' -Code 'noTenantDialPlan' `
                -What 'The user has no explicit tenant dial plan.' `
                -Why 'Dialed numbers may not normalize as administrators and users expect; the tenant service-country default still applies.' `
                -Fix 'Verify the tenant default or use update-user-calling-policies to assign the intended tenant dial plan.'
        }
        if ([string]::IsNullOrWhiteSpace($locationId)) {
            Add-DiagnosticFinding -Findings $findings -Severity 'critical' -Code 'noEmergencyLocation' `
                -What 'The user has no emergency location assignment.' `
                -Why 'Emergency calls may lack the dispatchable location required for routing and responder context.' `
                -Fix 'Use list-emergency-addresses, then update-user-emergency-location with a validated location.'
        }

        $status = if ($findings.Count -eq 0) { 'healthy' } else { 'issuesFound' }
        $summary = if ($findings.Count -eq 0) {
            "No voice configuration issues were found for $userUpn."
        }
        else {
            "Found $($findings.Count) voice configuration issue(s) for $userUpn."
        }

        $after = [ordered]@{
            userPrincipalName      = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $userUpn)
            status                 = $status
            phoneSystemLicensed    = $phoneSystemLicensed
            enterpriseVoiceEnabled = $enterpriseVoiceEnabled
            lineUri                = $lineUri
            onlineVoiceRoutingPolicy = $voiceRoutingPolicy
            tenantDialPlan         = $dialPlan
            emergencyLocationId    = if ([string]::IsNullOrWhiteSpace($locationId)) { $null } else { $locationId }
            findings               = @($findings)
        }

        return (Write-StageResult -Summary $summary -After $after)
    }
    default {
        throw "Tool 'diagnose-user-voice' does not implement stage '$Stage'."
    }
}