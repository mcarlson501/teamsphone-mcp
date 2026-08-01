BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stub so Pester can mock a cmdlet that ships with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsOnlineUser {
        param([string]$Identity, [string]$ErrorAction)
    }

    function New-StageInput {
        param([string]$UserUpn = 'jdoe@contoso.com')

        return (@{ input = @{ userUpn = $UserUpn }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5)
    }
}

Describe 'check-user-licensing' {
    Context 'execute stage' {
        It 'reports a fully licensed, voice-ready user' {
            Mock Get-CsOnlineUser {
                [PSCustomObject]@{
                    UserPrincipalName      = 'jdoe@contoso.com'
                    AccountEnabled         = $true
                    UsageLocation          = 'US'
                    EnterpriseVoiceEnabled = $true
                    LineUri                = 'tel:+15551234567'
                    FeatureTypes           = @('PhoneSystem', 'DomesticCalling', 'Teams')
                    AssignedPlan           = @(
                        [PSCustomObject]@{ Capability = 'MCOEV'; CapabilityStatus = 'Enabled'; AssignedTimestamp = '2026-06-01T00:00:00Z' }
                    )
                }
            }

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.summary | Should -Match 'voice-ready'
            $parsed.after.phoneSystemLicensed | Should -BeTrue
            $parsed.after.callingPlanLicensed | Should -BeTrue
            $parsed.after.voiceReady | Should -BeTrue
            $parsed.after.lineUri | Should -Be '+15551234567'
            $parsed.after.assignedPlans[0].capability | Should -Be 'MCOEV'
            $parsed.after.blockers | Should -HaveCount 0
        }

        It 'lists every blocker for an unlicensed user' {
            Mock Get-CsOnlineUser {
                [PSCustomObject]@{
                    UserPrincipalName      = 'newhire@contoso.com'
                    AccountEnabled         = $true
                    UsageLocation          = $null
                    EnterpriseVoiceEnabled = $false
                    LineUri                = $null
                    FeatureTypes           = @('Teams')
                    AssignedPlan           = @()
                }
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -UserUpn 'newhire@contoso.com') | ConvertFrom-Json

            $parsed.after.voiceReady | Should -BeFalse
            $parsed.after.phoneSystemLicensed | Should -BeFalse
            $parsed.after.blockers | Should -Contain 'noPhoneSystemLicense'
            $parsed.after.blockers | Should -Contain 'noUsageLocation'
            $parsed.after.blockers | Should -Contain 'enterpriseVoiceDisabled'
            $parsed.after.blockers | Should -Contain 'noPhoneNumberAssigned'
            $parsed.summary | Should -Match 'not voice-ready'
        }

        It 'tolerates a response without licensing properties' {
            Mock Get-CsOnlineUser { [PSCustomObject]@{ UserPrincipalName = 'sparse@contoso.com' } }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -UserUpn 'sparse@contoso.com') | ConvertFrom-Json

            $parsed.after.featureTypes | Should -HaveCount 0
            $parsed.after.voiceReady | Should -BeFalse
        }

        It 'retries once when the tenant throttles the request' {
            $global:ThrottleAttempts = 0
            Mock Get-CsOnlineUser {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                [PSCustomObject]@{ UserPrincipalName = 'jdoe@contoso.com'; FeatureTypes = @('PhoneSystem') }
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.phoneSystemLicensed | Should -BeTrue
        }

        It 'surfaces a terminating error when the user cannot be found' {
            Mock Get-CsOnlineUser { throw 'User not found.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput -UserUpn 'missing@contoso.com') } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage verify -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
