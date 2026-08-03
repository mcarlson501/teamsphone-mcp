BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    function Get-CsOnlineUser { param([string]$ResultSize, [string]$ErrorAction) }
    function Get-CsPhoneNumberAssignment { param([string]$ErrorAction) }
    function Get-CsOnlineLisLocation { param([string]$ErrorAction) }
    function New-Input { @{ input = @{}; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 4 }
}

Describe 'report-emergency-coverage' {
    It 'joins voice users to number assignments and validated locations' {
        Mock Get-CsOnlineUser {
            @(
                [PSCustomObject]@{ UserPrincipalName = 'covered@contoso.com'; EnterpriseVoiceEnabled = $true; LineUri = 'tel:+15550000001' },
                [PSCustomObject]@{ UserPrincipalName = 'missing@contoso.com'; EnterpriseVoiceEnabled = $true; LineUri = 'tel:+15550000002' },
                [PSCustomObject]@{ UserPrincipalName = 'ignored@contoso.com'; EnterpriseVoiceEnabled = $false }
            )
        }
        Mock Get-CsPhoneNumberAssignment {
            @(
                [PSCustomObject]@{ TelephoneNumber = '+15550000001'; LocationId = 'loc-1' },
                [PSCustomObject]@{ TelephoneNumber = '+15550000002'; LocationId = $null }
            )
        }
        Mock Get-CsOnlineLisLocation { [PSCustomObject]@{ LocationId = 'loc-1'; Description = 'HQ'; IsValidated = $true } }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.enterpriseVoiceUserCount | Should -Be 2
        $parsed.after.coveredCount | Should -Be 1
        $parsed.after.missingCount | Should -Be 1
        $parsed.after.findings[0].code | Should -Be 'voiceUsersMissingEmergencyLocation'
    }

    It 'distinguishes unknown and unvalidated location assignments' {
        Mock Get-CsOnlineUser {
            @(
                [PSCustomObject]@{ UserPrincipalName = 'unknown@contoso.com'; EnterpriseVoiceEnabled = $true; LineUri = 'tel:+15550000003' },
                [PSCustomObject]@{ UserPrincipalName = 'invalid@contoso.com'; EnterpriseVoiceEnabled = $true; LineUri = 'tel:+15550000004' }
            )
        }
        Mock Get-CsPhoneNumberAssignment {
            @(
                [PSCustomObject]@{ TelephoneNumber = '+15550000003'; LocationId = 'deleted' },
                [PSCustomObject]@{ TelephoneNumber = '+15550000004'; LocationId = 'loc-2' }
            )
        }
        Mock Get-CsOnlineLisLocation { [PSCustomObject]@{ LocationId = 'loc-2'; ValidationStatus = 'Invalid' } }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.unknownLocationCount | Should -Be 1
        $parsed.after.unvalidatedCount | Should -Be 1
    }
}