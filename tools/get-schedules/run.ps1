param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("snapshot", "preflight", "dryrun", "execute", "verify", "rollback")]
    [string]$Stage,

    [Parameter(Mandatory = $true)]
    [string]$InputJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

$script:WeekDays = @('Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday')

function ConvertTo-TimeRange {
    param($Range)

    return [ordered]@{
        start = [string](Get-PropertyValue -InputObject $Range -Name 'Start')
        end   = [string](Get-PropertyValue -InputObject $Range -Name 'End')
    }
}

function ConvertTo-WeeklyHours {
    param($WeeklySchedule)

    if ($null -eq $WeeklySchedule) { return @() }

    $days = @()
    foreach ($day in $script:WeekDays) {
        $ranges = @(Get-PropertyValue -InputObject $WeeklySchedule -Name "$($day)Hours" -Default @())
        if ($ranges.Count -eq 0) { continue }

        $days += [ordered]@{
            day    = $day
            ranges = @($ranges | ForEach-Object { ConvertTo-TimeRange -Range $_ })
        }
    }

    return $days
}

function Get-ReferencedScheduleMap {
    # Reverse lookup requires a full auto attendant enumeration, so it only runs
    # when the caller asks for used-only schedules.
    $attendants = @(Invoke-WithRetry -ScriptBlock { Get-CsAutoAttendant -ErrorAction Stop })

    $map = @{}
    foreach ($attendant in $attendants) {
        $name = [string](Get-PropertyValue -InputObject $attendant -Name 'Name')
        foreach ($association in @(Get-PropertyValue -InputObject $attendant -Name 'CallHandlingAssociations' -Default @())) {
            $scheduleId = [string](Get-PropertyValue -InputObject $association -Name 'ScheduleId')
            if ([string]::IsNullOrWhiteSpace($scheduleId)) { continue }

            if (-not $map.ContainsKey($scheduleId)) { $map[$scheduleId] = @() }
            if ($map[$scheduleId] -notcontains $name) { $map[$scheduleId] += $name }
        }
    }

    return $map
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$pagination = Get-PropertyValue -InputObject $payload -Name 'pagination'
$usedOnly = [bool](Get-PropertyValue -InputObject $toolInput -Name 'usedOnly' -Default $false)

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        $schedules = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineSchedule -ErrorAction Stop })
        $referenceMap = if ($usedOnly) { Get-ReferencedScheduleMap } else { $null }

        $matched = @()
        foreach ($schedule in $schedules) {
            $id = [string](Get-PropertyValue -InputObject $schedule -Name 'Id')
            # An `if` used as an expression collapses an empty array to $null,
            # so the default is assigned separately.
            $referencedBy = @()
            if ($null -ne $referenceMap -and $referenceMap.ContainsKey($id)) {
                $referencedBy = @($referenceMap[$id])
            }

            if ($usedOnly -and $referencedBy.Count -eq 0) { continue }

            $fixedSchedule = Get-PropertyValue -InputObject $schedule -Name 'FixedSchedule'
            $weeklySchedule = Get-PropertyValue -InputObject $schedule -Name 'WeeklyRecurrentSchedule'

            $matched += [ordered]@{
                id                = $id
                name              = [string](Get-PropertyValue -InputObject $schedule -Name 'Name')
                type              = [string](Get-PropertyValue -InputObject $schedule -Name 'Type')
                complementEnabled = Get-PropertyValue -InputObject $weeklySchedule -Name 'ComplementEnabled'
                weeklyHours       = ConvertTo-WeeklyHours -WeeklySchedule $weeklySchedule
                fixedRanges       = @(Get-PropertyValue -InputObject $fixedSchedule -Name 'DateTimeRanges' -Default @() | ForEach-Object { ConvertTo-TimeRange -Range $_ })
                referencedByAutoAttendants = if ($null -eq $referenceMap) { $null } else { $referencedBy }
            }
        }

        # Deterministic ordering keeps a continuation token pointing at the same
        # window across calls.
        $ordered = @($matched | Sort-Object -Property { "$($_.name)|$($_.id)" })
        $result = Select-StagePage -Items $ordered -Pagination $pagination

        $after = [ordered]@{
            totalMatched = $result.TotalCount
            usedOnly     = $usedOnly
            schedules    = @($result.Items)
        }

        $summary = "Matched $($result.TotalCount) schedules; returning $($result.Page.returnedCount)."
        return (Write-StageResult -Summary $summary -After $after -Page $result.Page)
    }
    default {
        throw "Tool 'get-schedules' does not implement stage '$Stage'."
    }
}
