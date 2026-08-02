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

function Get-UserState {
    param([Parameter(Mandatory = $true)][string]$Upn)

    $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    if ($null -eq $user) { throw "User '$Upn' was not found in the tenant." }
    $assignment = Get-PropertyValue -InputObject $user -Name 'CallingLineIdentity'
    if ($null -eq $assignment) { $assignment = Get-PropertyValue -InputObject $user -Name 'CallingLineIdentityPolicy' }

    return [ordered]@{
        userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        accountType       = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
        policyName        = Get-AssignedPolicyName -Value $assignment
    }
}

function Test-PolicyExists {
    param([Parameter(Mandatory = $true)][string]$Name)

    foreach ($policy in @(Invoke-WithRetry -ScriptBlock { Get-CsCallingLineIdentity -ErrorAction Stop })) {
        $identity = [string](Get-PropertyValue -InputObject $policy -Name 'Identity')
        if ($identity.StartsWith('Tag:', [System.StringComparison]::OrdinalIgnoreCase)) { $identity = $identity.Substring(4) }
        if ([string]::IsNullOrWhiteSpace($identity)) { $identity = [string](Get-PropertyValue -InputObject $policy -Name 'Name') }
        if ([string]::Equals($identity, $Name, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function New-Check {
    param([string]$Check, [bool]$Passed, [AllowNull()][string]$Detail)
    return [ordered]@{ check = $Check; passed = $Passed; detail = $Detail }
}

function Assert-Snapshot {
    param([AllowNull()][object]$Snapshot, [Parameter(Mandatory = $true)][string]$StageName)
    if ($null -eq $Snapshot) { throw "Stage '$StageName' requires the captured snapshot but none was supplied." }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$snapshot = Get-PropertyValue -InputObject $payload -Name 'snapshot'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')
$policyName = [string](Get-PropertyValue -InputObject $toolInput -Name 'policyName')

switch ($Stage) {
    'snapshot' {
        return (Write-StageSnapshot -State ([ordered]@{
            user         = Get-UserState -Upn $userUpn
            policyName   = $policyName
            policyExists = Test-PolicyExists -Name $policyName
            capturedAt   = (Get-Date).ToUniversalTime().ToString('o')
        }))
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $accountType = [string](Get-PropertyValue -InputObject $user -Name 'accountType')
        $isUser = [string]::IsNullOrWhiteSpace($accountType) -or [string]::Equals($accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)
        $exists = [bool](Get-PropertyValue -InputObject $snapshot -Name 'policyExists' -Default $false)
        $checks = @(
            (New-Check -Check 'target is a user account' -Passed $isUser -Detail $(if ($isUser) { "$userUpn is a user account." } else { "$userUpn is a $accountType." })),
            (New-Check -Check 'requested caller ID policy exists' -Passed $exists -Detail $(if ($exists) { "Caller ID policy '$policyName' exists." } else { "Caller ID policy '$policyName' was not found." }))
        )
        $failed = @($checks | Where-Object { -not $_.passed }).Count
        return (Write-StageResult -Summary $(if ($failed -eq 0) { "All preflight checks passed for assigning '$policyName' to $userUpn." } else { "$failed preflight check(s) failed; caller ID policy was not assigned." }) -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        return (Write-StageResult -Summary "Would assign caller ID policy '$policyName' to $userUpn." -After ([ordered]@{
            userPrincipalName = $userUpn
            policyName        = $policyName
            plannedCommands   = @("Grant-CsCallingLineIdentity -Identity '$userUpn' -PolicyName '$policyName'")
        }))
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $live = Get-UserState -Upn $userUpn
        if (-not [string]::IsNullOrWhiteSpace($live.accountType) -and -not [string]::Equals($live.accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$userUpn is not a user account; nothing was changed."
        }
        if (-not (Test-PolicyExists -Name $policyName)) { throw "Caller ID policy '$policyName' no longer exists; nothing was changed." }

        $originalPolicy = [string](Get-PropertyValue -InputObject $original -Name 'policyName')
        if (-not [string]::Equals([string]$live.policyName, $originalPolicy, [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals([string]$live.policyName, $policyName, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'The caller ID policy assignment changed since the snapshot; nothing was changed.'
        }
        if ([string]::Equals([string]$live.policyName, $policyName, [System.StringComparison]::OrdinalIgnoreCase)) {
            return (Write-StageResult -Summary "$userUpn already has caller ID policy '$policyName'." -After ([ordered]@{ user = $live; changed = $false }))
        }

        $null = Invoke-WithRetry -ScriptBlock { Grant-CsCallingLineIdentity -Identity $userUpn -PolicyName $policyName -ErrorAction Stop }
        return (Write-StageResult -Summary "Assigned caller ID policy '$policyName' to $userUpn." -After ([ordered]@{ user = Get-UserState -Upn $userUpn; changed = $true }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $assigned = [string]::Equals([string](Get-UserState -Upn $userUpn).policyName, $policyName, [System.StringComparison]::OrdinalIgnoreCase)
        $checks = @((New-Check -Check 'requested caller ID policy assigned' -Passed $assigned -Detail $(if ($assigned) { "$userUpn has caller ID policy '$policyName'." } else { "$userUpn does not have caller ID policy '$policyName'." })))
        return (Write-StageResult -Summary $(if ($assigned) { "Verified caller ID policy '$policyName' on $userUpn." } else { 'Caller ID policy verification failed.' }) -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $originalPolicy = Get-PropertyValue -InputObject $original -Name 'policyName'
        $live = Get-UserState -Upn $userUpn
        if (-not [string]::Equals([string]$live.policyName, [string]$originalPolicy, [System.StringComparison]::OrdinalIgnoreCase)) {
            $null = Invoke-WithRetry -ScriptBlock { Grant-CsCallingLineIdentity -Identity $userUpn -PolicyName $originalPolicy -ErrorAction Stop }
        }
        return (Write-StageResult -Summary "Restored the original caller ID policy assignment on $userUpn." -After (Get-UserState -Upn $userUpn))
    }

    default { throw "Tool 'set-caller-id-assignment' does not implement stage '$Stage'." }
}