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

function Test-NumberIsAssigned {
    param($Number)
    $status = [string](Get-PropertyValue -InputObject $Number -Name 'PstnAssignmentStatus')
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        return ($status -match '(?i)assigned' -and $status -notmatch '(?i)unassigned')
    }
    return $null -ne (Get-PropertyValue -InputObject $Number -Name 'AssignedPstnTargetId')
}

function Get-GroupKey {
    param($Number)
    $numberType = [string](Get-PropertyValue -InputObject $Number -Name 'NumberType' -Default 'unknown')
    $country = [string](Get-PropertyValue -InputObject $Number -Name 'IsoCountryCode' -Default 'unknown')
    if ([string]::IsNullOrWhiteSpace($numberType)) { $numberType = 'unknown' }
    if ([string]::IsNullOrWhiteSpace($country)) { $country = 'unknown' }
    return "$numberType|$country"
}

$null = Get-StageInput -InputJson $InputJson
switch ($Stage) {
    'execute' {
        $numbers = @(Invoke-WithRetry -ScriptBlock { Get-CsPhoneNumberAssignment -ErrorAction Stop })
        $groups = [ordered]@{}
        $assignedCount = 0
        foreach ($number in $numbers) {
            $key = Get-GroupKey -Number $number
            if (-not $groups.Contains($key)) {
                $parts = $key.Split('|', 2)
                $groups[$key] = [ordered]@{
                    numberType = $parts[0]
                    countryOrRegion = $parts[1]
                    total = 0
                    assigned = 0
                    available = 0
                    utilizationPercent = 0
                }
            }
            $group = $groups[$key]
            $group.total++
            if (Test-NumberIsAssigned -Number $number) {
                $group.assigned++
                $assignedCount++
            }
            else { $group.available++ }
        }

        $breakdown = @()
        foreach ($group in $groups.Values) {
            if ($group.total -gt 0) { $group.utilizationPercent = [Math]::Round(100 * $group.assigned / $group.total, 1) }
            $breakdown += $group
        }
        $total = $numbers.Count
        $availableCount = $total - $assignedCount
        $utilization = if ($total -eq 0) { 0 } else { [Math]::Round(100 * $assignedCount / $total, 1) }
        $warnings = @()
        if ($total -gt 0 -and $utilization -ge 90) {
            $warnings += [ordered]@{
                severity = 'warning'
                code = 'numberCapacityLow'
                what = "Phone number utilization is $utilization percent."
                why = 'New users may not have an available number when capacity is nearly exhausted.'
                fix = 'Acquire additional numbers from the carrier or release unused assignments with remove-phone-number.'
            }
        }

        $after = [ordered]@{
            total = $total
            assigned = $assignedCount
            available = $availableCount
            utilizationPercent = $utilization
            byTypeAndCountry = @($breakdown | Sort-Object -Property { "$($_.numberType)|$($_.countryOrRegion)" })
            forecast = $null
            forecastNote = 'Exhaustion forecasting requires historical carrier inventory snapshots, which are not available from the current Teams inventory read.'
            findings = $warnings
        }
        return (Write-StageResult -Summary "Number utilization is $utilization percent: $assignedCount assigned and $availableCount available." -After $after)
    }
    default { throw "Tool 'report-number-utilization' does not implement stage '$Stage'." }
}