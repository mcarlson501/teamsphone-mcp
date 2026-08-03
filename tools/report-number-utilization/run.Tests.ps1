BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    function Get-CsPhoneNumberAssignment { param([string]$ErrorAction) }
    function New-Input { @{ input = @{}; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 4 }
}

Describe 'report-number-utilization' {
    It 'aggregates assignments by type and country' {
        Mock Get-CsPhoneNumberAssignment {
            @(
                [PSCustomObject]@{ NumberType = 'CallingPlan'; IsoCountryCode = 'US'; PstnAssignmentStatus = 'UserAssigned' },
                [PSCustomObject]@{ NumberType = 'CallingPlan'; IsoCountryCode = 'US'; PstnAssignmentStatus = 'Unassigned' },
                [PSCustomObject]@{ NumberType = 'OperatorConnect'; IsoCountryCode = 'GB'; PstnAssignmentStatus = 'Unassigned' }
            )
        }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.total | Should -Be 3
        $parsed.after.assigned | Should -Be 1
        $parsed.after.available | Should -Be 2
        $parsed.after.byTypeAndCountry | Should -HaveCount 2
        $parsed.after.forecast | Should -BeNullOrEmpty
    }

    It 'warns when current capacity is at least ninety percent utilized' {
        Mock Get-CsPhoneNumberAssignment {
            1..10 | ForEach-Object { [PSCustomObject]@{ NumberType = 'CallingPlan'; IsoCountryCode = 'US'; PstnAssignmentStatus = 'UserAssigned' } }
        }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.findings[0].code | Should -Be 'numberCapacityLow'
    }
}