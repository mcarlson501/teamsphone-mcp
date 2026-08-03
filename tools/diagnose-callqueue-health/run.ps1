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

function Add-QueueFinding {
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
        code = $Code
        what = $What
        why = $Why
        fix = $Fix
    })
}

function Resolve-Queue {
    param([string]$Identity)

    if (Test-IsGuid -Value $Identity) {
        return @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -Identity $Identity -ErrorAction Stop })
    }

    $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -NameFilter $Identity -ErrorAction Stop })
    return @($queues | Where-Object {
        [string](Get-PropertyValue -InputObject $_ -Name 'Name') -eq $Identity
    })
}

function Test-TargetReachable {
    param($Target)

    if ($null -eq $Target) { return $false }

    $targetId = [string](Get-PropertyValue -InputObject $Target -Name 'Id')
    $targetType = [string](Get-PropertyValue -InputObject $Target -Name 'Type')
    if ([string]::IsNullOrWhiteSpace($targetId)) { return $false }

    try {
        switch -Regex ($targetType) {
            '^(?i)ApplicationEndpoint$' {
                $resolved = @(Invoke-WithRetry -ScriptBlock {
                    Get-CsOnlineApplicationInstance -Identity $targetId -ErrorAction Stop
                } | Where-Object { $null -ne $_ })
                return $resolved.Count -gt 0
            }
            '^(?i)User$' {
                $resolved = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $targetId -ErrorAction Stop }
                return $null -ne $resolved
            }
            '^(?i)Phone$' { return $null -ne (ConvertTo-E164Number -Value $targetId) }
            '^(?i)Voicemail$' { return $true }
            default { return $false }
        }
    }
    catch {
        return $false
    }
}

function Test-RequiresTarget {
    param([string]$Action)

    return -not [string]::IsNullOrWhiteSpace($Action) -and
        $Action -notmatch '^(?i)(Disconnect|SharedVoicemail)$'
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$callQueueIdentity = [string](Get-PropertyValue -InputObject $toolInput -Name 'callQueueIdentity')

switch ($Stage) {
    'execute' {
        $queues = @(Resolve-Queue -Identity $callQueueIdentity)
        if ($queues.Count -eq 0) { throw "Call queue '$callQueueIdentity' was not found." }
        if ($queues.Count -gt 1) {
            throw "Call queue '$callQueueIdentity' is ambiguous; $($queues.Count) queues share that name. Retry with the queue identity."
        }

        $queue = $queues[0]
        $queueName = [string](Get-PropertyValue -InputObject $queue -Name 'Name' -Default $callQueueIdentity)
        $findings = [System.Collections.ArrayList]::new()
        $agents = @(Get-PropertyValue -InputObject $queue -Name 'Agents' -Default @())
        $resolvedAgents = [System.Collections.ArrayList]::new()
        $optedInCount = 0

        foreach ($agent in $agents) {
            $objectId = [string](Get-PropertyValue -InputObject $agent -Name 'ObjectId')
            $optInValue = Get-PropertyValue -InputObject $agent -Name 'OptIn'
            $optedIn = $null -eq $optInValue -or [bool]$optInValue
            if ($optedIn) { $optedInCount++ }

            try {
                $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $objectId -ErrorAction Stop }
                [void]$resolvedAgents.Add([ordered]@{
                    objectId = $objectId
                    userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName')
                    optedIn = $optedIn
                    resolved = $true
                })
            }
            catch {
                [void]$resolvedAgents.Add([ordered]@{
                    objectId = $objectId
                    userPrincipalName = $null
                    optedIn = $optedIn
                    resolved = $false
                })
            }
        }

        $unresolvedCount = @($resolvedAgents | Where-Object { -not $_.resolved }).Count
        if ($agents.Count -eq 0) {
            Add-QueueFinding -Findings $findings -Severity 'critical' -Code 'noConfiguredAgents' `
                -What "Call queue '$queueName' has no configured agents." `
                -Why 'Calls entering the queue cannot reach a person.' `
                -Fix "Use update-callqueue-members to add reachable agents to '$queueName'."
        }
        elseif ($optedInCount -eq 0) {
            Add-QueueFinding -Findings $findings -Severity 'critical' -Code 'noOptedInAgents' `
                -What "All $($agents.Count) configured agents are opted out of '$queueName'." `
                -Why 'The queue has members but none are eligible to receive calls.' `
                -Fix 'Have at least one agent opt in, or disable opt-out if queue policy requires continuous coverage.'
        }

        if ($unresolvedCount -gt 0) {
            Add-QueueFinding -Findings $findings -Severity 'warning' -Code 'unresolvedAgents' `
                -What "$unresolvedCount configured agent(s) in '$queueName' could not be resolved." `
                -Why 'Deleted or inaccessible agent identities reduce effective queue capacity.' `
                -Fix "Remove stale members with update-callqueue-members and add valid replacements to '$queueName'."
        }

        $presenceBasedRouting = [bool](Get-PropertyValue -InputObject $queue -Name 'PresenceBasedRouting' -Default $false)
        if ($presenceBasedRouting -and $optedInCount -le 1) {
            Add-QueueFinding -Findings $findings -Severity 'warning' -Code 'presenceRoutingStarvationRisk' `
                -What "Presence-based routing is enabled and only $optedInCount agent(s) are opted in to '$queueName'." `
                -Why 'A busy, offline, or unavailable presence state can leave no eligible agent for incoming calls.' `
                -Fix 'Add opted-in coverage, review agent presence behavior, or disable presence-based routing if it is not required.'
        }

        foreach ($route in @(
            [ordered]@{ Name = 'overflow'; Action = [string](Get-PropertyValue -InputObject $queue -Name 'OverflowAction'); Target = Get-PropertyValue -InputObject $queue -Name 'OverflowActionTarget' },
            [ordered]@{ Name = 'timeout'; Action = [string](Get-PropertyValue -InputObject $queue -Name 'TimeoutAction'); Target = Get-PropertyValue -InputObject $queue -Name 'TimeoutActionTarget' }
        )) {
            if ((Test-RequiresTarget -Action $route.Action) -and -not (Test-TargetReachable -Target $route.Target)) {
                Add-QueueFinding -Findings $findings -Severity 'critical' -Code "$($route.Name)TargetUnreachable" `
                    -What "The $($route.Name) action '$($route.Action)' for '$queueName' has no reachable target." `
                    -Why "Calls that reach the $($route.Name) condition cannot complete the configured route." `
                    -Fix "Update the queue's $($route.Name) target to a valid user, resource account, phone number, or voicemail destination."
            }
        }

        $criticalCount = @($findings | Where-Object { $_.severity -eq 'critical' }).Count
        $warningCount = @($findings | Where-Object { $_.severity -eq 'warning' }).Count
        $status = if ($criticalCount -gt 0) { 'criticalIssues' } elseif ($warningCount -gt 0) { 'issuesFound' } else { 'healthy' }

        $after = [ordered]@{
            identity = [string](Get-PropertyValue -InputObject $queue -Name 'Identity')
            name = $queueName
            status = $status
            routingMethod = [string](Get-PropertyValue -InputObject $queue -Name 'RoutingMethod')
            presenceBasedRouting = $presenceBasedRouting
            allowOptOut = Get-PropertyValue -InputObject $queue -Name 'AllowOptOut'
            configuredAgentCount = $agents.Count
            optedInAgentCount = $optedInCount
            unresolvedAgentCount = $unresolvedCount
            agents = @($resolvedAgents)
            findings = @($findings)
        }

        return (Write-StageResult -Summary "Call queue '$queueName' health check found $criticalCount critical and $warningCount warning issue(s)." -After $after)
    }
    default {
        throw "Tool 'diagnose-callqueue-health' does not implement stage '$Stage'."
    }
}