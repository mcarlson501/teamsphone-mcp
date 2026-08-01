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

# Manifest numberType values mapped to the NumberType strings the Teams module
# reports. Matching is case-insensitive and prefix-based because the module has
# used both "CallingPlan" and "CallingPlan_..." style discriminators.
$script:NumberTypePatterns = @{
    callingPlan     = 'callingplan'
    operatorConnect = 'operatorconnect'
    directRouting   = 'directrouting'
}

function Test-NumberIsAssigned {
    param($Number)

    $status = Get-PropertyValue -InputObject $Number -Name 'PstnAssignmentStatus'
    if ($null -ne $status) {
        return ([string]$status -match '(?i)assigned' -and [string]$status -notmatch '(?i)unassigned')
    }

    return $null -ne (Get-PropertyValue -InputObject $Number -Name 'AssignedPstnTargetId')
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$pagination = Get-PropertyValue -InputObject $payload -Name 'pagination'

$assignmentStatus = [string](Get-PropertyValue -InputObject $toolInput -Name 'assignmentStatus' -Default 'all')
$numberPrefix = Get-PropertyValue -InputObject $toolInput -Name 'numberPrefix'
$numberType = Get-PropertyValue -InputObject $toolInput -Name 'numberType'

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        $numbers = @(Invoke-WithRetry -ScriptBlock { Get-CsPhoneNumberAssignment -ErrorAction Stop })

        $matched = @()
        foreach ($number in $numbers) {
            $telephoneNumber = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $number -Name 'TelephoneNumber'))
            if ($null -eq $telephoneNumber) { continue }

            $isAssigned = Test-NumberIsAssigned -Number $number
            if ($assignmentStatus -eq 'assigned' -and -not $isAssigned) { continue }
            if ($assignmentStatus -eq 'unassigned' -and $isAssigned) { continue }

            if (-not [string]::IsNullOrWhiteSpace($numberPrefix) -and -not $telephoneNumber.StartsWith([string]$numberPrefix, [StringComparison]::Ordinal)) {
                continue
            }

            $reportedType = [string](Get-PropertyValue -InputObject $number -Name 'NumberType' -Default '')
            if (-not [string]::IsNullOrWhiteSpace($numberType)) {
                $expected = $script:NumberTypePatterns[[string]$numberType]
                if (($reportedType -replace '[\s_\-]', '').ToLowerInvariant() -notlike "$expected*") { continue }
            }

            $matched += [ordered]@{
                telephoneNumber = $telephoneNumber
                numberType      = if ([string]::IsNullOrWhiteSpace($reportedType)) { $null } else { $reportedType }
                capability      = @(Get-PropertyValue -InputObject $number -Name 'Capability' -Default @())
                isoCountryCode  = Get-PropertyValue -InputObject $number -Name 'IsoCountryCode'
                city            = Get-PropertyValue -InputObject $number -Name 'City'
                assigned        = $isAssigned
                assignedTo      = Get-PropertyValue -InputObject $number -Name 'AssignedPstnTargetId'
                targetType      = Get-PropertyValue -InputObject $number -Name 'PstnAssignmentStatus'
            }
        }

        # Deterministic ordering keeps a continuation token pointing at the same
        # window across calls.
        $ordered = @($matched | Sort-Object -Property { $_.telephoneNumber })
        $assignedCount = @($ordered | Where-Object { $_.assigned }).Count

        $result = Select-StagePage -Items $ordered -Pagination $pagination

        $after = [ordered]@{
            totalMatched     = $result.TotalCount
            assignedCount    = $assignedCount
            unassignedCount  = $result.TotalCount - $assignedCount
            assignmentStatus = $assignmentStatus
            numbers          = @($result.Items)
        }

        $summary = "Matched $($result.TotalCount) phone numbers ($assignedCount assigned); returning $($result.Page.returnedCount)."
        return (Write-StageResult -Summary $summary -After $after -Page $result.Page)
    }
    default {
        throw "Tool 'list-phone-numbers' does not implement stage '$Stage'."
    }
}
