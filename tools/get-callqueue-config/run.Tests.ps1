BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stubs so Pester can mock cmdlets that ship with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsCallQueue {
        param([string]$Identity, [string]$NameFilter, [string]$ErrorAction)
    }

    function Get-CsOnlineUser {
        param([string]$Identity, [string]$ErrorAction)
    }

    function New-StageInput {
        param([string]$CallQueueIdentity = 'Sales Queue')

        return (@{ input = @{ callQueueIdentity = $CallQueueIdentity }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5)
    }

    # Mock bodies execute in the invoked script's scope, so fixture data is
    # exposed through a function rather than a $script: variable.
    function New-SampleQueue {
        [PSCustomObject]@{
            Identity             = '11111111-1111-1111-1111-111111111111'
            Name                 = 'Sales Queue'
            RoutingMethod        = 'Attendant'
            LanguageId           = 'en-US'
            AllowOptOut          = $true
            ConferenceMode       = $true
            PresenceBasedRouting = $false
            AgentAlertTime       = 30
            Agents               = @(
                [PSCustomObject]@{ ObjectId = '22222222-2222-2222-2222-222222222222' },
                [PSCustomObject]@{ ObjectId = '33333333-3333-3333-3333-333333333333' }
            )
            DistributionLists    = @('44444444-4444-4444-4444-444444444444')
            OverflowThreshold    = 50
            OverflowAction       = 'Forward'
            OverflowActionTarget = [PSCustomObject]@{ Id = '55555555-5555-5555-5555-555555555555'; Type = 'User' }
            TimeoutThreshold     = 60
            TimeoutAction        = 'Disconnect'
            ApplicationInstances = @('66666666-6666-6666-6666-666666666666')
        }
    }
}

Describe 'get-callqueue-config' {
    Context 'execute stage' {
        It 'returns the queue configuration with resolved agents' {
            Mock Get-CsCallQueue { New-SampleQueue }
            Mock Get-CsOnlineUser {
                [PSCustomObject]@{
                    UserPrincipalName = "agent-$Identity@contoso.com"
                    DisplayName       = 'Test Agent'
                }
            }

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.summary | Should -Match 'Sales Queue'
            $parsed.after.name | Should -Be 'Sales Queue'
            $parsed.after.routingMethod | Should -Be 'Attendant'
            $parsed.after.agentCount | Should -Be 2
            $parsed.after.agentsTruncated | Should -BeFalse
            $parsed.after.agents[0].userPrincipalName | Should -Match '@contoso.com'
            $parsed.after.agents[0].resolved | Should -BeTrue
            $parsed.after.overflowTarget.type | Should -Be 'User'
            $parsed.after.timeoutAction | Should -Be 'Disconnect'
            $parsed.after.timeoutTarget | Should -BeNullOrEmpty
            $parsed.after.resourceAccountIds | Should -HaveCount 1
        }

        It 'looks the queue up by identity when a GUID is supplied' {
            Mock Get-CsCallQueue { New-SampleQueue }
            Mock Get-CsOnlineUser { [PSCustomObject]@{ UserPrincipalName = 'a@contoso.com'; DisplayName = 'A' } }

            $null = & $script:RunScript -Stage execute -InputJson (New-StageInput -CallQueueIdentity '11111111-1111-1111-1111-111111111111')

            Should -Invoke Get-CsCallQueue -Times 1 -ParameterFilter { $null -ne $Identity }
        }

        It 'reports an unresolvable agent instead of failing the read' {
            Mock Get-CsCallQueue { New-SampleQueue }
            Mock Get-CsOnlineUser { throw 'User not found.' }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.agents[0].resolved | Should -BeFalse
            $parsed.after.agents[0].userPrincipalName | Should -BeNullOrEmpty
        }

        It 'throws when no queue matches the supplied name' {
            Mock Get-CsCallQueue { @() }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput -CallQueueIdentity 'Missing Queue') } | Should -Throw '*was not found*'
        }

        It 'throws when the supplied name is ambiguous' {
            Mock Get-CsCallQueue {
                @(
                    [PSCustomObject]@{ Identity = 'a'; Name = 'Sales Queue' },
                    [PSCustomObject]@{ Identity = 'b'; Name = 'Sales Queue' }
                )
            }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw '*ambiguous*'
        }

        It 'retries once when the tenant throttles the request' {
            $global:ThrottleAttempts = 0
            Mock Get-CsCallQueue {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                New-SampleQueue
            }
            Mock Get-CsOnlineUser { [PSCustomObject]@{ UserPrincipalName = 'a@contoso.com'; DisplayName = 'A' } }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.name | Should -Be 'Sales Queue'
        }

        It 'surfaces a terminating error when the read fails' {
            Mock Get-CsCallQueue { throw 'Access denied.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage preflight -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
