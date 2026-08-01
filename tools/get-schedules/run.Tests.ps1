BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stubs so Pester can mock cmdlets that ship with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsOnlineSchedule { param([string]$ErrorAction) }
    function Get-CsAutoAttendant { param([string]$ErrorAction) }

    function New-StageInput {
        param(
            [hashtable]$ToolInput = @{},
            $Pagination = $null
        )

        return (@{ input = $ToolInput; snapshot = $null; pagination = $Pagination } | ConvertTo-Json -Depth 5)
    }

    # Mock bodies execute in the invoked script's scope, so fixture data is
    # exposed through a function rather than a $script: variable.
    function New-SampleSchedules {
        @(
            [PSCustomObject]@{
                Id                     = 'sched-2'
                Name                   = 'EST Business Hours'
                Type                   = 'WeeklyRecurrence'
                WeeklyRecurrentSchedule = [PSCustomObject]@{
                    ComplementEnabled = $true
                    MondayHours       = @([PSCustomObject]@{ Start = '09:00:00'; End = '17:00:00' })
                    TuesdayHours      = @([PSCustomObject]@{ Start = '09:00:00'; End = '17:00:00' })
                    SaturdayHours     = @()
                }
            },
            [PSCustomObject]@{
                Id            = 'sched-1'
                Name          = 'Company Holidays'
                Type          = 'Fixed'
                FixedSchedule = [PSCustomObject]@{
                    DateTimeRanges = @([PSCustomObject]@{ Start = '2026-12-25T00:00:00'; End = '2026-12-26T00:00:00' })
                }
            },
            [PSCustomObject]@{
                Id   = 'sched-3'
                Name = 'Unused Schedule'
                Type = 'Fixed'
            }
        )
    }

    function New-SampleAttendants {
        @(
            [PSCustomObject]@{
                Name                     = 'Main Reception'
                CallHandlingAssociations = @(
                    [PSCustomObject]@{ Type = 'AfterHours'; ScheduleId = 'sched-2' },
                    [PSCustomObject]@{ Type = 'Holiday'; ScheduleId = 'sched-1' }
                )
            },
            [PSCustomObject]@{
                Name                     = 'HR Line'
                CallHandlingAssociations = @(
                    [PSCustomObject]@{ Type = 'AfterHours'; ScheduleId = 'sched-2' }
                )
            }
        )
    }
}

Describe 'get-schedules' {
    Context 'execute stage' {
        It 'returns every schedule in stable order with flattened hours' {
            Mock Get-CsOnlineSchedule { New-SampleSchedules }
            Mock Get-CsAutoAttendant { throw 'should not be called' }

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.after.totalMatched | Should -Be 3
            $parsed.after.schedules[0].name | Should -Be 'Company Holidays'
            # ConvertFrom-Json rehydrates ISO 8601 strings as [datetime].
            [datetime]$parsed.after.schedules[0].fixedRanges[0].start | Should -Be ([datetime]'2026-12-25T00:00:00')
            $parsed.after.schedules[1].name | Should -Be 'EST Business Hours'
            $parsed.after.schedules[1].complementEnabled | Should -BeTrue
            $parsed.after.schedules[1].weeklyHours | Should -HaveCount 2
            $parsed.after.schedules[1].weeklyHours[0].day | Should -Be 'Monday'
            $parsed.after.schedules[1].weeklyHours[0].ranges[0].end | Should -Be '17:00:00'
            $parsed.page.hasMore | Should -BeFalse
            Should -Invoke Get-CsAutoAttendant -Times 0
        }

        It 'restricts to referenced schedules and names the referencing attendants' {
            Mock Get-CsOnlineSchedule { New-SampleSchedules }
            Mock Get-CsAutoAttendant { New-SampleAttendants }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ usedOnly = $true }) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 2
            $parsed.after.usedOnly | Should -BeTrue
            $parsed.after.schedules[1].name | Should -Be 'EST Business Hours'
            $parsed.after.schedules[1].referencedByAutoAttendants | Should -HaveCount 2
            $parsed.after.schedules[0].referencedByAutoAttendants | Should -Be @('Main Reception')
        }

        It 'honours the host-supplied page window' {
            Mock Get-CsOnlineSchedule { New-SampleSchedules }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -Pagination @{ pageSize = 2; offset = 0 }) | ConvertFrom-Json

            $parsed.after.schedules | Should -HaveCount 2
            $parsed.page.hasMore | Should -BeTrue
            $parsed.page.nextOffset | Should -Be 2
        }

        It 'returns an empty page when the tenant has no schedules' {
            Mock Get-CsOnlineSchedule { @() }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 0
            $parsed.page.returnedCount | Should -Be 0
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'retries once when the tenant throttles the request' {
            $global:ThrottleAttempts = 0
            Mock Get-CsOnlineSchedule {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                New-SampleSchedules
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.totalMatched | Should -Be 3
        }

        It 'surfaces a terminating error when the read fails' {
            Mock Get-CsOnlineSchedule { throw 'Access denied.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage rollback -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
