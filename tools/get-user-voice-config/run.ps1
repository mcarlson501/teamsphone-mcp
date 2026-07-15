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

function Get-AssignedPolicyName {
    param($Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return $Value }

    $nameProperty = $Value.PSObject.Properties['Name']
    if ($null -ne $nameProperty -and $null -ne $nameProperty.Value) {
        return [string]$nameProperty.Value
    }

    return [string]$Value
}

$payload = Get-StageInput -InputJson $InputJson
$userUpn = $payload.input.userUpn

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $userUpn -ErrorAction Stop }

        $after = [ordered]@{
            userPrincipalName        = [string]$user.UserPrincipalName
            enterpriseVoiceEnabled   = [bool]$user.EnterpriseVoiceEnabled
            lineUri                  = if ($null -ne $user.LineUri) { [string]$user.LineUri } else { $null }
            onlineVoiceRoutingPolicy = Get-AssignedPolicyName -Value $user.OnlineVoiceRoutingPolicy
            tenantDialPlan           = Get-AssignedPolicyName -Value $user.TenantDialPlan
            teamsCallingPolicy       = Get-AssignedPolicyName -Value $user.TeamsCallingPolicy
        }

        return (Write-StageResult -Summary "Retrieved voice configuration for $userUpn." -After $after)
    }
    default {
        throw "Tool 'get-user-voice-config' does not implement stage '$Stage'."
    }
}
