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

$script:PolicyNames = @('onlineVoiceRoutingPolicy', 'tenantDialPlan', 'teamsCallingPolicy')

function Get-UserState {
    param([Parameter(Mandatory = $true)][string]$Upn)

    $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    if ($null -eq $user) { throw "User '$Upn' was not found in the tenant." }

    return [ordered]@{
        userPrincipalName        = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        accountType              = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
        onlineVoiceRoutingPolicy = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'OnlineVoiceRoutingPolicy')
        tenantDialPlan           = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'TenantDialPlan')
        teamsCallingPolicy       = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'TeamsCallingPolicy')
    }
}

function Get-RequestedPolicies {
    param([Parameter(Mandatory = $true)][object]$InputObject)

    return [ordered]@{
        onlineVoiceRoutingPolicy = [string](Get-PropertyValue -InputObject $InputObject -Name 'onlineVoiceRoutingPolicy')
        tenantDialPlan           = [string](Get-PropertyValue -InputObject $InputObject -Name 'tenantDialPlan')
        teamsCallingPolicy       = [string](Get-PropertyValue -InputObject $InputObject -Name 'teamsCallingPolicy')
    }
}

function Test-PolicyExists {
    param([Parameter(Mandatory = $true)][string]$Name, [object[]]$Policies)

    foreach ($policy in $Policies) {
        $identity = [string](Get-PropertyValue -InputObject $policy -Name 'Identity')
        if ($identity.StartsWith('Tag:', [System.StringComparison]::OrdinalIgnoreCase)) { $identity = $identity.Substring(4) }
        if ([string]::IsNullOrWhiteSpace($identity)) { $identity = [string](Get-PropertyValue -InputObject $policy -Name 'Name') }
        if ([string]::Equals($identity, $Name, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }

    return $false
}

function Get-PolicyAvailability {
    param([Parameter(Mandatory = $true)][object]$RequestedPolicies)

    $voiceRouting = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name 'onlineVoiceRoutingPolicy')
    $dialPlan = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name 'tenantDialPlan')
    $calling = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name 'teamsCallingPolicy')

    return [ordered]@{
        onlineVoiceRoutingPolicy = [string]::IsNullOrWhiteSpace($voiceRouting) -or (Test-PolicyExists -Name $voiceRouting -Policies @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineVoiceRoutingPolicy -ErrorAction Stop }))
        tenantDialPlan           = [string]::IsNullOrWhiteSpace($dialPlan) -or (Test-PolicyExists -Name $dialPlan -Policies @(Invoke-WithRetry -ScriptBlock { Get-CsTenantDialPlan -ErrorAction Stop }))
        teamsCallingPolicy       = [string]::IsNullOrWhiteSpace($calling) -or (Test-PolicyExists -Name $calling -Policies @(Invoke-WithRetry -ScriptBlock { Get-CsTeamsCallingPolicy -ErrorAction Stop }))
    }
}

function Set-PolicyAssignment {
    param(
        [Parameter(Mandatory = $true)][string]$PolicyType,
        [Parameter(Mandatory = $true)][string]$Upn,
        [AllowNull()][string]$PolicyName
    )

    switch ($PolicyType) {
        'onlineVoiceRoutingPolicy' {
            $null = Invoke-WithRetry -ScriptBlock { Grant-CsOnlineVoiceRoutingPolicy -Identity $Upn -PolicyName $PolicyName -ErrorAction Stop }
        }
        'tenantDialPlan' {
            $null = Invoke-WithRetry -ScriptBlock { Grant-CsTenantDialPlan -Identity $Upn -PolicyName $PolicyName -ErrorAction Stop }
        }
        'teamsCallingPolicy' {
            $null = Invoke-WithRetry -ScriptBlock { Grant-CsTeamsCallingPolicy -Identity $Upn -PolicyName $PolicyName -ErrorAction Stop }
        }
        default { throw "Unsupported policy type '$PolicyType'." }
    }
}

function Restore-Policies {
    param(
        [Parameter(Mandatory = $true)][object]$Original,
        [Parameter(Mandatory = $true)][string]$Upn,
        [Parameter(Mandatory = $true)][object]$RequestedPolicies
    )

    $live = Get-UserState -Upn $Upn
    foreach ($name in $script:PolicyNames) {
        $requested = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name $name)
        if ([string]::IsNullOrWhiteSpace($requested)) { continue }

        $originalPolicy = Get-PropertyValue -InputObject $Original -Name $name
        $livePolicy = Get-PropertyValue -InputObject $live -Name $name
        if (-not [string]::Equals([string]$livePolicy, [string]$originalPolicy, [System.StringComparison]::OrdinalIgnoreCase)) {
            Set-PolicyAssignment -PolicyType $name -Upn $Upn -PolicyName $originalPolicy
        }
    }
}

function Test-RequestedPoliciesAssigned {
    param([Parameter(Mandatory = $true)][object]$User, [Parameter(Mandatory = $true)][object]$RequestedPolicies)

    foreach ($name in $script:PolicyNames) {
        $requested = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name $name)
        if ([string]::IsNullOrWhiteSpace($requested)) { continue }
        if (-not [string]::Equals([string](Get-PropertyValue -InputObject $User -Name $name), $requested, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
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
$requestedPolicies = Get-RequestedPolicies -InputObject $toolInput

switch ($Stage) {
    'snapshot' {
        return (Write-StageSnapshot -State ([ordered]@{
            user               = Get-UserState -Upn $userUpn
            requestedPolicies  = $requestedPolicies
            policyAvailability = Get-PolicyAvailability -RequestedPolicies $requestedPolicies
            capturedAt         = (Get-Date).ToUniversalTime().ToString('o')
        }))
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $availability = Get-PropertyValue -InputObject $snapshot -Name 'policyAvailability'
        $accountType = [string](Get-PropertyValue -InputObject $user -Name 'accountType')
        $isUser = [string]::IsNullOrWhiteSpace($accountType) -or [string]::Equals($accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)

        $requestedCount = @($script:PolicyNames | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string](Get-PropertyValue -InputObject $requestedPolicies -Name $_))
        }).Count
        $missing = @($script:PolicyNames | Where-Object {
            -not [bool](Get-PropertyValue -InputObject $availability -Name $_ -Default $false)
        } | ForEach-Object { [string](Get-PropertyValue -InputObject $requestedPolicies -Name $_) })

        $checks = @(
            (New-Check -Check 'target is a user account' -Passed $isUser -Detail $(if ($isUser) { "$userUpn is a user account." } else { "$userUpn is a $accountType." })),
            (New-Check -Check 'at least one policy assignment is requested' -Passed ($requestedCount -gt 0) -Detail $(if ($requestedCount -gt 0) { "$requestedCount policy assignment(s) requested." } else { 'No policy assignment was requested.' })),
            (New-Check -Check 'requested policies exist' -Passed ($missing.Count -eq 0) -Detail $(if ($missing.Count -eq 0) { 'All requested policies exist.' } else { "Missing policies: $($missing -join ', ')." }))
        )

        $failed = @($checks | Where-Object { -not $_.passed }).Count
        return (Write-StageResult -Summary $(if ($failed -eq 0) { "All preflight checks passed for updating policies on $userUpn." } else { "$failed preflight check(s) failed; policies were not updated." }) -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $plannedCommands = @()
        $after = [ordered]@{ userPrincipalName = $userUpn }

        foreach ($name in $script:PolicyNames) {
            $requested = [string](Get-PropertyValue -InputObject $requestedPolicies -Name $name)
            $current = Get-PropertyValue -InputObject $user -Name $name
            $after[$name] = if ([string]::IsNullOrWhiteSpace($requested)) { $current } else { $requested }
            if ([string]::IsNullOrWhiteSpace($requested) -or [string]::Equals([string]$current, $requested, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

            $command = switch ($name) {
                'onlineVoiceRoutingPolicy' { 'Grant-CsOnlineVoiceRoutingPolicy' }
                'tenantDialPlan' { 'Grant-CsTenantDialPlan' }
                'teamsCallingPolicy' { 'Grant-CsTeamsCallingPolicy' }
            }
            $plannedCommands += "$command -Identity '$userUpn' -PolicyName '$requested'"
        }
        $after.plannedCommands = $plannedCommands
        return (Write-StageResult -Summary "Would update $($plannedCommands.Count) policy assignment(s) on $userUpn." -After $after)
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $live = Get-UserState -Upn $userUpn
        if (-not [string]::IsNullOrWhiteSpace($live.accountType) -and -not [string]::Equals($live.accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$userUpn is not a user account; nothing was changed."
        }

        $availability = Get-PolicyAvailability -RequestedPolicies $requestedPolicies
        foreach ($name in $script:PolicyNames) {
            if (-not [bool](Get-PropertyValue -InputObject $availability -Name $name -Default $false)) {
                throw 'A requested policy no longer exists; nothing was changed.'
            }
        }

        $changed = @()
        try {
            foreach ($name in $script:PolicyNames) {
                $requested = [string](Get-PropertyValue -InputObject $requestedPolicies -Name $name)
                if ([string]::IsNullOrWhiteSpace($requested)) { continue }
                $current = [string](Get-PropertyValue -InputObject $live -Name $name)
                if ([string]::Equals($current, $requested, [System.StringComparison]::OrdinalIgnoreCase)) { continue }

                Set-PolicyAssignment -PolicyType $name -Upn $userUpn -PolicyName $requested
                $changed += $name
            }
        }
        catch {
            $originalError = $_
            try { Restore-Policies -Original $original -Upn $userUpn -RequestedPolicies $requestedPolicies }
            catch { throw 'Policy assignment failed and automatic restoration also failed; manual intervention is required.' }
            throw $originalError
        }

        return (Write-StageResult -Summary $(if ($changed.Count -eq 0) { "$userUpn already has the requested policies." } else { "Updated $($changed.Count) policy assignment(s) on $userUpn." }) -After ([ordered]@{ user = Get-UserState -Upn $userUpn; changedPolicies = $changed; changed = $changed.Count -gt 0 }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $assigned = Test-RequestedPoliciesAssigned -User (Get-UserState -Upn $userUpn) -RequestedPolicies $requestedPolicies
        $checks = @((New-Check -Check 'requested policies assigned' -Passed $assigned -Detail $(if ($assigned) { 'All requested policies are assigned.' } else { 'One or more requested policies are not assigned.' })))
        return (Write-StageResult -Summary $(if ($assigned) { "Verified requested policies on $userUpn." } else { 'Policy assignment verification failed.' }) -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'user'
        Restore-Policies -Original $original -Upn $userUpn -RequestedPolicies $requestedPolicies
        return (Write-StageResult -Summary "Restored the original policy assignments on $userUpn." -After (Get-UserState -Upn $userUpn))
    }

    default { throw "Tool 'update-user-calling-policies' does not implement stage '$Stage'." }
}