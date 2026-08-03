BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    function Get-CsOnlineUser { param([string]$ResultSize, [string]$ErrorAction) }
    function New-Input { @{ input = @{}; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 4 }
}

Describe 'report-license-utilization' {
    It 'counts observed license assignments and flags unused Phone System licenses' {
        Mock Get-CsOnlineUser {
            @(
                [PSCustomObject]@{ UserPrincipalName = 'active@contoso.com'; FeatureTypes = @('PhoneSystem', 'DomesticCalling'); EnterpriseVoiceEnabled = $true },
                [PSCustomObject]@{ UserPrincipalName = 'wasted@contoso.com'; FeatureTypes = @('MCOEV'); EnterpriseVoiceEnabled = $false },
                [PSCustomObject]@{ UserPrincipalName = 'teams@contoso.com'; FeatureTypes = @('Teams'); EnterpriseVoiceEnabled = $false }
            )
        }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.observedUserCount | Should -Be 3
        $parsed.after.phoneSystemAssigned | Should -Be 2
        $parsed.after.callingPlanAssigned | Should -Be 1
        $parsed.after.wastedCount | Should -Be 1
        $parsed.after.findings[0].code | Should -Be 'licensedUsersNotVoiceEnabled'
        $parsed.after.availablePhoneSystemLicenses | Should -BeNullOrEmpty
    }
}