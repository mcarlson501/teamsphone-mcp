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

function Get-ResultValue {
    param($Result, [string[]]$Names)

    foreach ($name in $Names) {
        $value = Get-PropertyValue -InputObject $Result -Name $name
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) {
            return $value
        }
    }

    return $null
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')
$dialedNumber = [string](Get-PropertyValue -InputObject $toolInput -Name 'dialedNumber')
$callerNumber = [string](Get-PropertyValue -InputObject $toolInput -Name 'callerNumber')

switch ($Stage) {
    'execute' {
        $effectivePlan = Invoke-WithRetry -ScriptBlock {
            Get-CsEffectiveTenantDialPlan -Identity $userUpn -ErrorAction Stop
        }

        $testResult = if ([string]::IsNullOrWhiteSpace($callerNumber)) {
            Invoke-WithRetry -ScriptBlock {
                Test-CsEffectiveTenantDialPlan -Identity $userUpn -DialedNumber $dialedNumber -ErrorAction Stop
            }
        }
        else {
            Invoke-WithRetry -ScriptBlock {
                Test-CsEffectiveTenantDialPlan -Identity $userUpn -DialedNumber $dialedNumber -CallerNumber $callerNumber -ErrorAction Stop
            }
        }

        $normalizedNumber = [string](Get-ResultValue -Result $testResult -Names @('TranslatedNumber', 'NormalizedNumber', 'Translation'))
        $matchingRule = [string](Get-ResultValue -Result $testResult -Names @('MatchingRule', 'RuleName', 'Name'))
        $planIdentity = [string](Get-ResultValue -Result $effectivePlan -Names @('Identity', 'Name'))
        $findings = @()

        if ([string]::IsNullOrWhiteSpace($normalizedNumber)) {
            $findings += [ordered]@{
                severity = 'warning'
                code = 'numberDidNotNormalize'
                what = "Dialed string '$dialedNumber' did not match a normalization rule for $userUpn."
                why = 'Teams may send the original digits or reject the call when the effective dial plan cannot normalize the input.'
                fix = 'Review the effective tenant dial plan and add or correct a normalization rule before retrying the call.'
            }
        }

        $after = [ordered]@{
            userPrincipalName = $userUpn
            dialedNumber = $dialedNumber
            callerNumber = if ([string]::IsNullOrWhiteSpace($callerNumber)) { $null } else { $callerNumber }
            effectiveDialPlan = if ([string]::IsNullOrWhiteSpace($planIdentity)) { $null } else { $planIdentity }
            normalizedNumber = if ([string]::IsNullOrWhiteSpace($normalizedNumber)) { $null } else { $normalizedNumber }
            matchingRule = if ([string]::IsNullOrWhiteSpace($matchingRule)) { $null } else { $matchingRule }
            matched = -not [string]::IsNullOrWhiteSpace($normalizedNumber)
            findings = $findings
        }

        $summary = if ($after.matched) {
            "Dialed string '$dialedNumber' normalizes to '$normalizedNumber' for $userUpn."
        }
        else {
            "Dialed string '$dialedNumber' did not normalize for $userUpn."
        }

        return (Write-StageResult -Summary $summary -After $after)
    }
    default {
        throw "Tool 'test-dialplan-number' does not implement stage '$Stage'."
    }
}