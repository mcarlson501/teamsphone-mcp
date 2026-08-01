BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stub so Pester can mock a cmdlet that ships with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsPhoneNumberAssignment {
        param([string]$ErrorAction)
    }

    function New-StageInput {
        param(
            [hashtable]$ToolInput = @{},
            $Pagination = $null
        )

        return (@{ input = $ToolInput; snapshot = $null; pagination = $Pagination } | ConvertTo-Json -Depth 5)
    }

    # Mock bodies execute in the invoked script's scope, so fixture data is
    # exposed through a function rather than a $script: variable.
    function New-SampleNumbers {
        @(
        [PSCustomObject]@{
            TelephoneNumber      = '+15551110002'
            NumberType           = 'CallingPlan'
            Capability           = @('UserAssignment')
            IsoCountryCode       = 'US'
            City                 = 'Seattle'
            PstnAssignmentStatus = 'UserAssigned'
            AssignedPstnTargetId = 'jdoe@contoso.com'
        },
        [PSCustomObject]@{
            TelephoneNumber      = '+15551110001'
            NumberType           = 'OperatorConnect'
            Capability           = @('UserAssignment')
            IsoCountryCode       = 'US'
            City                 = 'Seattle'
            PstnAssignmentStatus = 'Unassigned'
        },
        [PSCustomObject]@{
            TelephoneNumber      = '+442071110003'
            NumberType           = 'DirectRouting'
            Capability           = @('UserAssignment')
            IsoCountryCode       = 'GB'
            City                 = 'London'
            PstnAssignmentStatus = 'Unassigned'
        }
        )
    }
}

Describe 'list-phone-numbers' {
    Context 'execute stage' {
        It 'returns every number in stable order with page metadata' {
            Mock Get-CsPhoneNumberAssignment { New-SampleNumbers }

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.after.totalMatched | Should -Be 3
            $parsed.after.assignedCount | Should -Be 1
            $parsed.after.unassignedCount | Should -Be 2
            $parsed.after.numbers[0].telephoneNumber | Should -Be '+15551110001'
            $parsed.after.numbers[2].telephoneNumber | Should -Be '+442071110003'
            $parsed.page.returnedCount | Should -Be 3
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'filters to assigned numbers only' {
            Mock Get-CsPhoneNumberAssignment { New-SampleNumbers }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ assignmentStatus = 'assigned' }) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 1
            $parsed.after.numbers[0].telephoneNumber | Should -Be '+15551110002'
            $parsed.after.numbers[0].assigned | Should -BeTrue
        }

        It 'filters to unassigned numbers only' {
            Mock Get-CsPhoneNumberAssignment { New-SampleNumbers }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ assignmentStatus = 'unassigned' }) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 2
            $parsed.after.assignedCount | Should -Be 0
        }

        It 'filters by number prefix and number type' {
            Mock Get-CsPhoneNumberAssignment { New-SampleNumbers }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ numberPrefix = '+1555' }) | ConvertFrom-Json
            $parsed.after.totalMatched | Should -Be 2

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ numberType = 'directRouting' }) | ConvertFrom-Json
            $parsed.after.totalMatched | Should -Be 1
            $parsed.after.numbers[0].telephoneNumber | Should -Be '+442071110003'
        }

        It 'honours the host-supplied page window' {
            Mock Get-CsPhoneNumberAssignment { New-SampleNumbers }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -Pagination @{ pageSize = 2; offset = 0 }) | ConvertFrom-Json

            $parsed.after.numbers | Should -HaveCount 2
            $parsed.page.returnedCount | Should -Be 2
            $parsed.page.hasMore | Should -BeTrue
            $parsed.page.nextOffset | Should -Be 2

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -Pagination @{ pageSize = 2; offset = 2 }) | ConvertFrom-Json
            $parsed.page.returnedCount | Should -Be 1
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'returns an empty page when the tenant has no numbers' {
            Mock Get-CsPhoneNumberAssignment { @() }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 0
            $parsed.page.returnedCount | Should -Be 0
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'retries once when the tenant throttles the request' {
            $global:ThrottleAttempts = 0
            Mock Get-CsPhoneNumberAssignment {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                New-SampleNumbers
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.totalMatched | Should -Be 3
        }

        It 'surfaces a terminating error when the read fails' {
            Mock Get-CsPhoneNumberAssignment { throw 'Access denied.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage rollback -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
