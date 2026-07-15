BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stub so Pester can mock a cmdlet that ships with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsOnlineUser {
        param(
            [string]$Identity,
            [string]$ErrorAction
        )
    }
}

Describe 'get-user-voice-config' {
    Context 'execute stage' {
        It 'returns the user voice configuration as a single JSON result' {
            Mock Get-CsOnlineUser {
                [PSCustomObject]@{
                    UserPrincipalName        = 'jdoe@contoso.com'
                    EnterpriseVoiceEnabled   = $true
                    LineUri                  = 'tel:+15551234567'
                    OnlineVoiceRoutingPolicy = [PSCustomObject]@{ Name = 'US-Unrestricted' }
                    TenantDialPlan           = 'US-NY'
                    TeamsCallingPolicy       = [PSCustomObject]@{ Name = 'AllowCalling' }
                }
            }

            $inputJson = @{ input = @{ userUpn = 'jdoe@contoso.com' }; snapshot = $null } | ConvertTo-Json -Depth 5
            $result = & $script:RunScript -Stage execute -InputJson $inputJson

            $result | Should -HaveCount 1

            $parsed = $result | ConvertFrom-Json
            $parsed.summary | Should -Match 'jdoe@contoso.com'
            $parsed.after.userPrincipalName | Should -Be 'jdoe@contoso.com'
            $parsed.after.enterpriseVoiceEnabled | Should -BeTrue
            $parsed.after.lineUri | Should -Be 'tel:+15551234567'
            $parsed.after.onlineVoiceRoutingPolicy | Should -Be 'US-Unrestricted'
            $parsed.after.tenantDialPlan | Should -Be 'US-NY'
            $parsed.after.teamsCallingPolicy | Should -Be 'AllowCalling'
        }

        It 'surfaces a terminating error when the user cannot be found' {
            Mock Get-CsOnlineUser { throw 'User not found.' }

            $inputJson = @{ input = @{ userUpn = 'missing@contoso.com' }; snapshot = $null } | ConvertTo-Json -Depth 5

            { & $script:RunScript -Stage execute -InputJson $inputJson } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            $inputJson = @{ input = @{ userUpn = 'jdoe@contoso.com' }; snapshot = $null } | ConvertTo-Json -Depth 5

            { & $script:RunScript -Stage rollback -InputJson $inputJson } | Should -Throw
        }
    }
}
