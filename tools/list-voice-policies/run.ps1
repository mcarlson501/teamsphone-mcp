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

function Get-PolicyName {
    param($Policy)

    $identity = [string](Get-PropertyValue -InputObject $Policy -Name 'Identity' -Default '')
    if ($identity.StartsWith('Tag:', [StringComparison]::OrdinalIgnoreCase)) {
        return $identity.Substring(4)
    }

    if ([string]::IsNullOrWhiteSpace($identity)) {
        return [string](Get-PropertyValue -InputObject $Policy -Name 'Name')
    }

    return $identity
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$policyType = [string](Get-PropertyValue -InputObject $toolInput -Name 'policyType' -Default 'all')

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage. Only the
        # requested policy families are queried so a narrow request stays cheap.
        $voiceRoutingPolicies = @()
        $tenantDialPlans = @()
        $callingPolicies = @()
        $voicemailPolicies = @()

        if ($policyType -in @('all', 'routing')) {
            foreach ($policy in @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineVoiceRoutingPolicy -ErrorAction Stop })) {
                $voiceRoutingPolicies += [ordered]@{
                    name             = Get-PolicyName -Policy $policy
                    description      = [string](Get-PropertyValue -InputObject $policy -Name 'Description')
                    onlinePstnUsages = @(Get-PropertyValue -InputObject $policy -Name 'OnlinePstnUsages' -Default @() | ForEach-Object { [string]$_ })
                }
            }
        }

        if ($policyType -in @('all', 'dialPlan')) {
            foreach ($policy in @(Invoke-WithRetry -ScriptBlock { Get-CsTenantDialPlan -ErrorAction Stop })) {
                $tenantDialPlans += [ordered]@{
                    name                  = Get-PolicyName -Policy $policy
                    simpleName            = [string](Get-PropertyValue -InputObject $policy -Name 'SimpleName')
                    description           = [string](Get-PropertyValue -InputObject $policy -Name 'Description')
                    externalAccessPrefix  = [string](Get-PropertyValue -InputObject $policy -Name 'ExternalAccessPrefix')
                    optimizeDeviceDialing = Get-PropertyValue -InputObject $policy -Name 'OptimizeDeviceDialing'
                    normalizationRuleCount = @(Get-PropertyValue -InputObject $policy -Name 'NormalizationRules' -Default @()).Count
                }
            }
        }

        if ($policyType -in @('all', 'calling')) {
            foreach ($policy in @(Invoke-WithRetry -ScriptBlock { Get-CsTeamsCallingPolicy -ErrorAction Stop })) {
                $callingPolicies += [ordered]@{
                    name                       = Get-PolicyName -Policy $policy
                    description                = [string](Get-PropertyValue -InputObject $policy -Name 'Description')
                    allowPrivateCalling        = Get-PropertyValue -InputObject $policy -Name 'AllowPrivateCalling'
                    allowVoicemail             = [string](Get-PropertyValue -InputObject $policy -Name 'AllowVoicemail')
                    allowCallForwardingToUser  = Get-PropertyValue -InputObject $policy -Name 'AllowCallForwardingToUser'
                    allowCallForwardingToPhone = Get-PropertyValue -InputObject $policy -Name 'AllowCallForwardingToPhone'
                    busyOnBusyEnabledType      = [string](Get-PropertyValue -InputObject $policy -Name 'BusyOnBusyEnabledType')
                }
            }
        }

        if ($policyType -in @('all', 'voicemail')) {
            foreach ($policy in @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineVoicemailPolicy -ErrorAction Stop })) {
                $voicemailPolicies += [ordered]@{
                    name                    = Get-PolicyName -Policy $policy
                    description             = [string](Get-PropertyValue -InputObject $policy -Name 'Description')
                    enableTranscription     = Get-PropertyValue -InputObject $policy -Name 'EnableTranscription'
                    enableTranscriptionProfanityMasking = Get-PropertyValue -InputObject $policy -Name 'EnableTranscriptionProfanityMasking'
                    maximumRecordingLength  = [string](Get-PropertyValue -InputObject $policy -Name 'MaximumRecordingLength')
                    shareData               = [string](Get-PropertyValue -InputObject $policy -Name 'ShareData')
                }
            }
        }

        $after = [ordered]@{
            policyType               = $policyType
            voiceRoutingPolicyCount  = $voiceRoutingPolicies.Count
            tenantDialPlanCount      = $tenantDialPlans.Count
            teamsCallingPolicyCount  = $callingPolicies.Count
            voicemailPolicyCount     = $voicemailPolicies.Count
            voiceRoutingPolicies     = @($voiceRoutingPolicies | Sort-Object -Property { $_.name })
            tenantDialPlans          = @($tenantDialPlans | Sort-Object -Property { $_.name })
            teamsCallingPolicies     = @($callingPolicies | Sort-Object -Property { $_.name })
            voicemailPolicies        = @($voicemailPolicies | Sort-Object -Property { $_.name })
        }

        $summary = "Voice policies: $($voiceRoutingPolicies.Count) routing, $($tenantDialPlans.Count) dial plans, $($callingPolicies.Count) calling, $($voicemailPolicies.Count) voicemail."
        return (Write-StageResult -Summary $summary -After $after)
    }
    default {
        throw "Tool 'list-voice-policies' does not implement stage '$Stage'."
    }
}
