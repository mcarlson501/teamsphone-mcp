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

# Agent identities are resolved one call at a time, so large queues are capped to
# keep a tier-0 read inside its timeout and away from tenant throttling limits.
$script:MaxResolvedAgents = 50

function Resolve-AgentIdentity {
    param([string]$ObjectId)

    try {
        $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $ObjectId -ErrorAction Stop }
    }
    catch {
        return [ordered]@{ objectId = $ObjectId; userPrincipalName = $null; resolved = $false }
    }

    return [ordered]@{
        objectId          = $ObjectId
        userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName')
        displayName       = [string](Get-PropertyValue -InputObject $user -Name 'DisplayName')
        resolved          = $true
    }
}

function ConvertTo-CallTarget {
    param($Target)

    if ($null -eq $Target) { return $null }

    return [ordered]@{
        id   = [string](Get-PropertyValue -InputObject $Target -Name 'Id')
        type = [string](Get-PropertyValue -InputObject $Target -Name 'Type')
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$callQueueIdentity = [string](Get-PropertyValue -InputObject $toolInput -Name 'callQueueIdentity')

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        if (Test-IsGuid -Value $callQueueIdentity) {
            $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -Identity $callQueueIdentity -ErrorAction Stop })
        }
        else {
            $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -NameFilter $callQueueIdentity -ErrorAction Stop })
            $queues = @($queues | Where-Object {
                    [string](Get-PropertyValue -InputObject $_ -Name 'Name') -eq $callQueueIdentity
                })
        }

        if ($queues.Count -eq 0) {
            throw "Call queue '$callQueueIdentity' was not found."
        }

        if ($queues.Count -gt 1) {
            throw "Call queue '$callQueueIdentity' is ambiguous; $($queues.Count) queues share that name. Retry with the queue identity."
        }

        $queue = $queues[0]

        $agentIds = @()
        foreach ($agent in @(Get-PropertyValue -InputObject $queue -Name 'Agents' -Default @())) {
            $objectId = Get-PropertyValue -InputObject $agent -Name 'ObjectId'
            if ($null -ne $objectId) { $agentIds += [string]$objectId }
        }

        $agentIds = @($agentIds | Select-Object -Unique | Sort-Object)
        $resolvedAgents = @()
        foreach ($objectId in @($agentIds | Select-Object -First $script:MaxResolvedAgents)) {
            $resolvedAgents += Resolve-AgentIdentity -ObjectId $objectId
        }

        $applicationInstances = @(Get-PropertyValue -InputObject $queue -Name 'ApplicationInstances' -Default @())

        $after = [ordered]@{
            identity                = [string](Get-PropertyValue -InputObject $queue -Name 'Identity')
            name                    = [string](Get-PropertyValue -InputObject $queue -Name 'Name')
            routingMethod           = [string](Get-PropertyValue -InputObject $queue -Name 'RoutingMethod')
            languageId              = [string](Get-PropertyValue -InputObject $queue -Name 'LanguageId')
            allowOptOut             = Get-PropertyValue -InputObject $queue -Name 'AllowOptOut'
            conferenceMode          = Get-PropertyValue -InputObject $queue -Name 'ConferenceMode'
            presenceBasedRouting    = Get-PropertyValue -InputObject $queue -Name 'PresenceBasedRouting'
            agentAlertTimeSeconds   = Get-PropertyValue -InputObject $queue -Name 'AgentAlertTime'
            agentCount              = $agentIds.Count
            agentsTruncated         = $agentIds.Count -gt $script:MaxResolvedAgents
            agents                  = $resolvedAgents
            distributionListCount   = @(Get-PropertyValue -InputObject $queue -Name 'DistributionLists' -Default @()).Count
            overflowThreshold       = Get-PropertyValue -InputObject $queue -Name 'OverflowThreshold'
            overflowAction          = [string](Get-PropertyValue -InputObject $queue -Name 'OverflowAction')
            overflowTarget          = ConvertTo-CallTarget -Target (Get-PropertyValue -InputObject $queue -Name 'OverflowActionTarget')
            timeoutThresholdSeconds = Get-PropertyValue -InputObject $queue -Name 'TimeoutThreshold'
            timeoutAction           = [string](Get-PropertyValue -InputObject $queue -Name 'TimeoutAction')
            timeoutTarget           = ConvertTo-CallTarget -Target (Get-PropertyValue -InputObject $queue -Name 'TimeoutActionTarget')
            musicOnHoldFileId       = [string](Get-PropertyValue -InputObject $queue -Name 'MusicOnHoldFileDownloadUri')
            useDefaultMusicOnHold   = Get-PropertyValue -InputObject $queue -Name 'UseDefaultMusicOnHold'
            resourceAccountIds      = @($applicationInstances | ForEach-Object { [string]$_ })
        }

        $summary = "Retrieved call queue '$($after.name)' with $($after.agentCount) agents."
        return (Write-StageResult -Summary $summary -After $after)
    }
    default {
        throw "Tool 'get-callqueue-config' does not implement stage '$Stage'."
    }
}
