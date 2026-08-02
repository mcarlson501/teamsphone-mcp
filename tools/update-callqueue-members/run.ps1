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

function Get-CallQueueState {
    param([Parameter(Mandatory = $true)][string]$QueueIdentity)

    if (Test-IsGuid -Value $QueueIdentity) {
        $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -Identity $QueueIdentity -ErrorAction Stop })
    } else {
        $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -NameFilter $QueueIdentity -ErrorAction Stop })
        $queues = @($queues | Where-Object {
            [string]::Equals([string](Get-PropertyValue -InputObject $_ -Name 'Name'), $QueueIdentity, [System.StringComparison]::OrdinalIgnoreCase)
        })
    }

    if ($queues.Count -eq 0) { throw "Call queue '$QueueIdentity' was not found." }
    if ($queues.Count -gt 1) { throw "Call queue '$QueueIdentity' is ambiguous; retry with its identity." }

    $queue = $queues[0]
    $agentIds = @()
    foreach ($agent in @(Get-PropertyValue -InputObject $queue -Name 'Agents' -Default @())) {
        $objectId = Get-PropertyValue -InputObject $agent -Name 'ObjectId'
        if ($null -ne $objectId) { $agentIds += [string]$objectId }
    }

    return [ordered]@{
        identity            = [string](Get-PropertyValue -InputObject $queue -Name 'Identity')
        name                = [string](Get-PropertyValue -InputObject $queue -Name 'Name')
        agentObjectIds      = @($agentIds | Sort-Object -Unique)
        distributionListIds = @(Get-PropertyValue -InputObject $queue -Name 'DistributionLists' -Default @() | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    }
}

function Resolve-Agent {
    param([Parameter(Mandatory = $true)][string]$Upn)

    try {
        $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    } catch {
        return [ordered]@{ userPrincipalName = $Upn; objectId = $null; accountType = $null; resolved = $false }
    }

    if ($null -eq $user) {
        return [ordered]@{ userPrincipalName = $Upn; objectId = $null; accountType = $null; resolved = $false }
    }

    $objectId = Get-PropertyValue -InputObject $user -Name 'Identity'
    if ($null -eq $objectId) { $objectId = Get-PropertyValue -InputObject $user -Name 'ObjectId' }
    return [ordered]@{
        userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        objectId          = if ($null -ne $objectId) { [string]$objectId } else { $null }
        accountType       = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
        resolved          = $null -ne $objectId
    }
}

function Resolve-Agents {
    param([string[]]$Upns)
    return @($Upns | ForEach-Object { Resolve-Agent -Upn $_ })
}

function Test-SetEqual {
    param([object[]]$Left, [object[]]$Right)

    $leftValues = @($Left | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    $rightValues = @($Right | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    if ($leftValues.Count -ne $rightValues.Count) { return $false }
    return @(Compare-Object -ReferenceObject $leftValues -DifferenceObject $rightValues).Count -eq 0
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
$callQueueIdentity = [string](Get-PropertyValue -InputObject $toolInput -Name 'callQueueIdentity')
$requestedUpns = @(Get-PropertyValue -InputObject $toolInput -Name 'agentUserUpns' -Default @() | ForEach-Object { [string]$_ })

switch ($Stage) {
    'snapshot' {
        return (Write-StageSnapshot -State ([ordered]@{
            queue           = Get-CallQueueState -QueueIdentity $callQueueIdentity
            requestedAgents = Resolve-Agents -Upns $requestedUpns
            capturedAt      = (Get-Date).ToUniversalTime().ToString('o')
        }))
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $queue = Get-PropertyValue -InputObject $snapshot -Name 'queue'
        $agents = @(Get-PropertyValue -InputObject $snapshot -Name 'requestedAgents' -Default @())
        $unique = @($requestedUpns | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique).Count -eq $requestedUpns.Count
        $resolved = @($agents | Where-Object { -not [bool]$_.resolved }).Count -eq 0
        $users = @($agents | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.accountType) -and
            -not [string]::Equals([string]$_.accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -eq 0
        $directOnly = @(Get-PropertyValue -InputObject $queue -Name 'distributionListIds' -Default @()).Count -eq 0

        $checks = @(
            (New-Check -Check 'requested agents are unique' -Passed $unique -Detail $(if ($unique) { 'No duplicate agent UPNs were supplied.' } else { 'Duplicate agent UPNs were supplied.' })),
            (New-Check -Check 'requested agents exist' -Passed $resolved -Detail $(if ($resolved) { 'All requested agents resolve to tenant users.' } else { 'One or more requested agents could not be resolved.' })),
            (New-Check -Check 'requested agents are user accounts' -Passed $users -Detail $(if ($users) { 'All requested agents are user accounts.' } else { 'One or more requested agents are not user accounts.' })),
            (New-Check -Check 'queue uses direct user membership only' -Passed $directOnly -Detail $(if ($directOnly) { 'The queue has no distribution-list agents.' } else { 'The queue has distribution-list agents and cannot be safely replaced by this tool.' }))
        )
        $failed = @($checks | Where-Object { -not $_.passed }).Count
        return (Write-StageResult -Summary $(if ($failed -eq 0) { "All preflight checks passed for updating '$($queue.name)'." } else { "$failed preflight check(s) failed; queue membership was not updated." }) -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $queue = Get-PropertyValue -InputObject $snapshot -Name 'queue'
        $agents = @(Get-PropertyValue -InputObject $snapshot -Name 'requestedAgents' -Default @())
        $desiredIds = @($agents | ForEach-Object { [string]$_.objectId } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
        $after = [ordered]@{
            identity           = [string]$queue.identity
            name               = [string]$queue.name
            agentUserUpns      = @($requestedUpns | Sort-Object -Unique)
            agentObjectIds     = $desiredIds
            plannedCommands    = @("Set-CsCallQueue -Identity '$($queue.identity)' -Users <resolved agent object IDs>")
        }
        return (Write-StageResult -Summary "Would replace direct membership on '$($queue.name)' with $($desiredIds.Count) agent(s)." -After $after)
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'queue'
        $live = Get-CallQueueState -QueueIdentity ([string]$original.identity)
        if (-not (Test-SetEqual -Left @($live.agentObjectIds) -Right @($original.agentObjectIds))) {
            throw 'The queue membership changed since the snapshot; nothing was changed.'
        }
        if (@($live.distributionListIds).Count -gt 0) {
            throw 'The queue now has distribution-list agents; nothing was changed.'
        }

        $agents = Resolve-Agents -Upns $requestedUpns
        if (@($agents | Where-Object { -not [bool]$_.resolved -or -not [string]::Equals([string]$_.accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
            throw 'A requested agent no longer resolves to a user account; nothing was changed.'
        }
        $desiredIds = @($agents | ForEach-Object { [string]$_.objectId } | Sort-Object -Unique)
        if (Test-SetEqual -Left @($live.agentObjectIds) -Right $desiredIds) {
            return (Write-StageResult -Summary "'$($live.name)' already has the requested direct membership." -After ([ordered]@{ queue = $live; changed = $false }))
        }

        $null = Invoke-WithRetry -ScriptBlock { Set-CsCallQueue -Identity $live.identity -Users $desiredIds -ErrorAction Stop }
        return (Write-StageResult -Summary "Updated '$($live.name)' to $($desiredIds.Count) direct agent(s)." -After ([ordered]@{ queue = Get-CallQueueState -QueueIdentity ([string]$live.identity); changed = $true }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $queue = Get-PropertyValue -InputObject $snapshot -Name 'queue'
        $agents = Resolve-Agents -Upns $requestedUpns
        $desiredIds = @($agents | ForEach-Object { [string]$_.objectId } | Sort-Object -Unique)
        $live = Get-CallQueueState -QueueIdentity ([string]$queue.identity)
        $matches = Test-SetEqual -Left @($live.agentObjectIds) -Right $desiredIds
        $checks = @((New-Check -Check 'queue direct user membership matches the requested agents' -Passed $matches -Detail $(if ($matches) { "'$($live.name)' has the requested direct agents." } else { "'$($live.name)' does not have the requested direct agents." })))
        return (Write-StageResult -Summary $(if ($matches) { "Verified direct membership on '$($live.name)'." } else { 'Queue membership verification failed.' }) -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'queue'
        $live = Get-CallQueueState -QueueIdentity ([string]$original.identity)
        if (@($live.distributionListIds).Count -gt 0) {
            throw 'The queue now has distribution-list agents; automatic rollback was not attempted.'
        }
        if (-not (Test-SetEqual -Left @($live.agentObjectIds) -Right @($original.agentObjectIds))) {
            $originalIds = @($original.agentObjectIds)
            $null = Invoke-WithRetry -ScriptBlock { Set-CsCallQueue -Identity $live.identity -Users $originalIds -ErrorAction Stop }
        }
        return (Write-StageResult -Summary "Restored the original direct membership on '$($live.name)'." -After (Get-CallQueueState -QueueIdentity ([string]$live.identity)))
    }

    default { throw "Tool 'update-callqueue-members' does not implement stage '$Stage'." }
}