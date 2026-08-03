BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    function Get-CsCallQueue { param([string]$Identity, [string]$NameFilter, [string]$ErrorAction) }
    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsOnlineApplicationInstance { param([string]$Identity, [string]$ErrorAction) }
    function New-Input { @{ input = @{ callQueueIdentity = 'Support' }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5 }
    function New-HealthyQueue {
        [PSCustomObject]@{
            Identity = '11111111-1111-1111-1111-111111111111'
            Name = 'Support'
            RoutingMethod = 'RoundRobin'
            PresenceBasedRouting = $false
            AllowOptOut = $true
            Agents = @(
                [PSCustomObject]@{ ObjectId = 'agent-1'; OptIn = $true },
                [PSCustomObject]@{ ObjectId = 'agent-2'; OptIn = $true }
            )
            OverflowAction = 'Forward'
            OverflowActionTarget = [PSCustomObject]@{ Id = 'overflow@contoso.com'; Type = 'User' }
            TimeoutAction = 'Disconnect'
            TimeoutActionTarget = $null
        }
    }
}

Describe 'diagnose-callqueue-health' {
    BeforeEach {
        Mock Get-CsCallQueue { New-HealthyQueue }
        Mock Get-CsOnlineUser { [PSCustomObject]@{ UserPrincipalName = $Identity } }
        Mock Get-CsOnlineApplicationInstance { [PSCustomObject]@{ ObjectId = $Identity } }
    }

    It 'reports a queue with reachable opted-in agents as healthy' {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.status | Should -Be 'healthy'
        $parsed.after.configuredAgentCount | Should -Be 2
        $parsed.after.optedInAgentCount | Should -Be 2
        $parsed.after.findings | Should -HaveCount 0
    }

    It 'flags a queue with no configured agents' {
        Mock Get-CsCallQueue {
            $queue = New-HealthyQueue
            $queue.Agents = @()
            $queue
        }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.status | Should -Be 'criticalIssues'
        $parsed.after.findings.code | Should -Contain 'noConfiguredAgents'
    }

    It 'flags opted-out and unresolved agents with presence starvation risk' {
        Mock Get-CsCallQueue {
            $queue = New-HealthyQueue
            $queue.PresenceBasedRouting = $true
            $queue.Agents = @(
                [PSCustomObject]@{ ObjectId = 'deleted-agent'; OptIn = $false }
            )
            $queue
        }
        Mock Get-CsOnlineUser { throw 'User not found.' }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.findings.code | Should -Contain 'noOptedInAgents'
        $parsed.after.findings.code | Should -Contain 'unresolvedAgents'
        $parsed.after.findings.code | Should -Contain 'presenceRoutingStarvationRisk'
    }

    It 'flags unreachable overflow and timeout targets' {
        Mock Get-CsCallQueue {
            $queue = New-HealthyQueue
            $queue.OverflowActionTarget = [PSCustomObject]@{ Id = 'deleted-user'; Type = 'User' }
            $queue.TimeoutAction = 'Forward'
            $queue.TimeoutActionTarget = [PSCustomObject]@{ Id = 'missing-ra'; Type = 'ApplicationEndpoint' }
            $queue
        }
        Mock Get-CsOnlineUser {
            if ($Identity -in @('agent-1', 'agent-2')) { return [PSCustomObject]@{ UserPrincipalName = $Identity } }
            throw 'User not found.'
        }
        Mock Get-CsOnlineApplicationInstance { throw 'Resource account not found.' }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.findings.code | Should -Contain 'overflowTargetUnreachable'
        $parsed.after.findings.code | Should -Contain 'timeoutTargetUnreachable'
    }

    It 'throws when the queue cannot be found' {
        Mock Get-CsCallQueue { @() }

        { & $script:RunScript -Stage execute -InputJson (New-Input) } | Should -Throw '*was not found*'
    }

    It 'throws for an unsupported stage' {
        { & $script:RunScript -Stage preflight -InputJson (New-Input) } | Should -Throw
    }
}