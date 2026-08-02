BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stubs so Pester can mock cmdlets that ship with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsOnlineVoiceRoutingPolicy { param([string]$ErrorAction) }
    function Get-CsTenantDialPlan { param([string]$ErrorAction) }
    function Get-CsTeamsCallingPolicy { param([string]$ErrorAction) }
    function Get-CsOnlineVoicemailPolicy { param([string]$ErrorAction) }

    function New-StageInput {
        param([hashtable]$ToolInput = @{})

        return (@{ input = $ToolInput; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5)
    }

    function Set-DefaultPolicyMocks {
        Mock Get-CsOnlineVoiceRoutingPolicy {
            @(
                [PSCustomObject]@{ Identity = 'Tag:US-Unrestricted'; Description = 'Full PSTN'; OnlinePstnUsages = @('US-Domestic', 'US-International') },
                [PSCustomObject]@{ Identity = 'Global'; Description = $null; OnlinePstnUsages = @() }
            )
        }
        Mock Get-CsTenantDialPlan {
            @([PSCustomObject]@{ Identity = 'Tag:US-NY'; SimpleName = 'US-NY'; Description = 'New York'; NormalizationRules = @(1, 2, 3); ExternalAccessPrefix = '9' })
        }
        Mock Get-CsTeamsCallingPolicy {
            @([PSCustomObject]@{ Identity = 'Tag:AllowCalling'; AllowPrivateCalling = $true; AllowVoicemail = 'UserOverride'; BusyOnBusyEnabledType = 'Enabled' })
        }
        Mock Get-CsOnlineVoicemailPolicy {
            @([PSCustomObject]@{ Identity = 'Global'; EnableTranscription = $true; MaximumRecordingLength = '00:05:00' })
        }
    }
}

Describe 'list-voice-policies' {
    Context 'execute stage' {
        It 'returns every policy family by default' {
            Set-DefaultPolicyMocks

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.summary | Should -Match '2 routing'
            $parsed.after.voiceRoutingPolicyCount | Should -Be 2
            $parsed.after.tenantDialPlanCount | Should -Be 1
            $parsed.after.teamsCallingPolicyCount | Should -Be 1
            $parsed.after.voicemailPolicyCount | Should -Be 1
            $parsed.after.voiceRoutingPolicies[0].name | Should -Be 'Global'
            $parsed.after.voiceRoutingPolicies[1].name | Should -Be 'US-Unrestricted'
            $parsed.after.voiceRoutingPolicies[1].onlinePstnUsages | Should -HaveCount 2
            $parsed.after.tenantDialPlans[0].normalizationRuleCount | Should -Be 3
        }

        It 'queries only the requested policy family' {
            Set-DefaultPolicyMocks

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ policyType = 'routing' }) | ConvertFrom-Json

            $parsed.after.voiceRoutingPolicyCount | Should -Be 2
            $parsed.after.tenantDialPlanCount | Should -Be 0
            $parsed.after.teamsCallingPolicies | Should -HaveCount 0
            Should -Invoke Get-CsTenantDialPlan -Times 0
            Should -Invoke Get-CsTeamsCallingPolicy -Times 0
            Should -Invoke Get-CsOnlineVoicemailPolicy -Times 0
        }

        It 'returns empty families when the tenant has no policies' {
            Mock Get-CsOnlineVoiceRoutingPolicy { @() }
            Mock Get-CsTenantDialPlan { @() }
            Mock Get-CsTeamsCallingPolicy { @() }
            Mock Get-CsOnlineVoicemailPolicy { @() }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.voiceRoutingPolicyCount | Should -Be 0
            $parsed.after.voicemailPolicies | Should -HaveCount 0
        }

        It 'retries once when the tenant throttles the request' {
            Set-DefaultPolicyMocks
            $global:ThrottleAttempts = 0
            Mock Get-CsOnlineVoiceRoutingPolicy {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                @([PSCustomObject]@{ Identity = 'Tag:US-Unrestricted'; OnlinePstnUsages = @() })
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ policyType = 'routing' }) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.voiceRoutingPolicyCount | Should -Be 1
        }

        It 'surfaces a terminating error when a policy read fails' {
            Set-DefaultPolicyMocks
            Mock Get-CsTeamsCallingPolicy { throw 'Access denied.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage rollback -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
