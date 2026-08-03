BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    function Get-CsOnlineUser { param([string]$ResultSize, [string]$ErrorAction) }
    function New-Input { param([string]$Format = 'json'); @{ input = @{ format = $Format }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 4 }
}

Describe 'report-policy-assignments' {
    BeforeEach {
        Mock Get-CsOnlineUser {
            @(
                [PSCustomObject]@{ UserPrincipalName = 'b@contoso.com'; OnlineVoiceRoutingPolicy = $null; TenantDialPlan = 'US'; TeamsCallingPolicy = 'Allow'; CallingLineIdentityPolicy = 'Main'; OnlineVoicemailPolicy = 'Default' },
                [PSCustomObject]@{ UserPrincipalName = 'a@contoso.com'; OnlineVoiceRoutingPolicy = [PSCustomObject]@{ Name = 'Route' }; TenantDialPlan = $null }
            )
        }
    }

    It 'returns a stable structured policy matrix and unassigned counts' {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.userCount | Should -Be 2
        $parsed.after.assignments[0].userPrincipalName | Should -Be 'a@contoso.com'
        $parsed.after.assignments[0].onlineVoiceRoutingPolicy | Should -Be 'Route'
        $parsed.after.unassignedCounts.onlineVoiceRoutingPolicy | Should -Be 1
        $parsed.after.report | Should -BeNullOrEmpty
    }

    It 'exports the matrix as CSV or Markdown' -ForEach @(
        @{ Format = 'csv'; Expected = 'userPrincipalName,voiceRoutingPolicy' },
        @{ Format = 'markdown'; Expected = '# Teams Phone policy assignments' }
    ) {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input -Format $Format) | ConvertFrom-Json
        $parsed.after.report | Should -Match ([regex]::Escape($Expected))
    }
}