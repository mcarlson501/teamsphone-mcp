BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    function Get-CsOnlineUser {
        param([string]$Identity, [string]$ErrorAction)
    }
    function Get-CsPhoneNumberAssignment {
        param([string]$TelephoneNumber, [string]$ErrorAction)
    }

    function New-StageInput {
        return (@{ input = @{ userUpn = 'jdoe@contoso.com' }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5)
    }
}

Describe 'diagnose-user-voice' {
    Context 'execute stage' {
        It 'reports a healthy fully configured user' {
            Mock Get-CsOnlineUser {
                [PSCustomObject]@{
                    UserPrincipalName      = 'jdoe@contoso.com'
                    FeatureTypes           = @('PhoneSystem')
                    EnterpriseVoiceEnabled = $true
                    LineUri                = 'tel:+15551234567'
                    OnlineVoiceRoutingPolicy = [PSCustomObject]@{ Name = 'US-Unrestricted' }
                    TenantDialPlan         = [PSCustomObject]@{ Name = 'US-NY' }
                }
            }
            Mock Get-CsPhoneNumberAssignment {
                [PSCustomObject]@{ TelephoneNumber = $TelephoneNumber; LocationId = 'location-1' }
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.status | Should -Be 'healthy'
            $parsed.after.findings | Should -HaveCount 0
            $parsed.after.lineUri | Should -Be '+15551234567'
            $parsed.after.emergencyLocationId | Should -Be 'location-1'
            $parsed.after.onlineVoiceRoutingPolicy | Should -Be 'US-Unrestricted'
            $parsed.summary | Should -Match 'No voice configuration issues'
        }

        It 'returns all findings in remediation order with actionable text' {
            Mock Get-CsOnlineUser {
                [PSCustomObject]@{
                    UserPrincipalName      = 'jdoe@contoso.com'
                    FeatureTypes           = @('Teams')
                    EnterpriseVoiceEnabled = $false
                    LineUri                = $null
                    OnlineVoiceRoutingPolicy = $null
                    TenantDialPlan         = $null
                    LocationId             = $null
                }
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.status | Should -Be 'issuesFound'
            $parsed.after.findings.code | Should -Be @(
                'noPhoneSystemLicense',
                'enterpriseVoiceDisabled',
                'noPhoneNumberAssigned',
                'noVoiceRoutingPolicy',
                'noTenantDialPlan',
                'noEmergencyLocation'
            )
            $parsed.after.findings[0].severity | Should -Be 'critical'
            $parsed.after.findings[3].severity | Should -Be 'warning'
            foreach ($finding in $parsed.after.findings) {
                $finding.what | Should -Not -BeNullOrEmpty
                $finding.why | Should -Not -BeNullOrEmpty
                $finding.fix | Should -Not -BeNullOrEmpty
            }
        }

        It 'treats absent diagnostic properties as findings instead of failing' {
            Mock Get-CsOnlineUser { [PSCustomObject]@{ UserPrincipalName = 'jdoe@contoso.com' } }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.findings | Should -HaveCount 6
        }

        It 'retries a throttled tenant read' {
            $global:DiagnosticAttempts = 0
            Mock Get-CsOnlineUser {
                $global:DiagnosticAttempts++
                if ($global:DiagnosticAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                [PSCustomObject]@{ UserPrincipalName = 'jdoe@contoso.com' }
            }

            & $script:RunScript -Stage execute -InputJson (New-StageInput) | Out-Null

            $global:DiagnosticAttempts | Should -Be 2
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage verify -InputJson (New-StageInput) } | Should -Throw
        }
    }
}