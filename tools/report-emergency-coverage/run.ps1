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

$payload = Get-StageInput -InputJson $InputJson
$pagination = Get-PropertyValue -InputObject $payload -Name 'pagination'

switch ($Stage) {
    'execute' {
        $users = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -ResultSize ([int]::MaxValue) -ErrorAction Stop })
        $assignments = @(Invoke-WithRetry -ScriptBlock { Get-CsPhoneNumberAssignment -ErrorAction Stop })
        $locations = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineLisLocation -ErrorAction Stop })

        $assignmentByNumber = @{}
        foreach ($assignment in $assignments) {
            $number = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $assignment -Name 'TelephoneNumber'))
            if ($null -ne $number) { $assignmentByNumber[$number] = $assignment }
        }
        $locationById = @{}
        foreach ($location in $locations) {
            $id = [string](Get-PropertyValue -InputObject $location -Name 'LocationId')
            if (-not [string]::IsNullOrWhiteSpace($id)) { $locationById[$id] = $location }
        }

        $coverage = @()
        foreach ($user in $users) {
            if (-not [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)) { continue }
            $upn = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName')
            $number = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
            $assignment = if ($null -ne $number -and $assignmentByNumber.ContainsKey($number)) { $assignmentByNumber[$number] } else { $null }
            $locationId = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'LocationId') } else { $null }
            $location = if (-not [string]::IsNullOrWhiteSpace($locationId) -and $locationById.ContainsKey($locationId)) { $locationById[$locationId] } else { $null }
            $coverageStatus = if ([string]::IsNullOrWhiteSpace($locationId)) {
                'missing'
            }
            elseif ($null -eq $location) {
                'unknownLocation'
            }
            elseif (-not (Test-TeamsEmergencyLocationValidated -Location $location)) {
                'unvalidated'
            }
            else { 'covered' }

            $coverage += [ordered]@{
                userPrincipalName = $upn
                phoneNumber = $number
                locationId = if ([string]::IsNullOrWhiteSpace($locationId)) { $null } else { $locationId }
                locationDescription = if ($null -ne $location) { [string](Get-PropertyValue -InputObject $location -Name 'Description') } else { $null }
                coverageStatus = $coverageStatus
            }
        }

        $ordered = @($coverage | Sort-Object -Property { $_.userPrincipalName })
        $coveredCount = @($ordered | Where-Object { $_.coverageStatus -eq 'covered' }).Count
        $missingCount = @($ordered | Where-Object { $_.coverageStatus -eq 'missing' }).Count
        $unknownCount = @($ordered | Where-Object { $_.coverageStatus -eq 'unknownLocation' }).Count
        $unvalidatedCount = @($ordered | Where-Object { $_.coverageStatus -eq 'unvalidated' }).Count
        $findings = @()
        if ($missingCount -gt 0) {
            $findings += [ordered]@{
                severity = 'critical'; code = 'voiceUsersMissingEmergencyLocation'
                what = "$missingCount enterprise-voice user(s) have no emergency location assignment."
                why = 'Emergency calls may lack a dispatchable location for routing and responder context.'
                fix = 'Use list-emergency-addresses, then update-user-emergency-location for each affected user.'
            }
        }
        if (($unknownCount + $unvalidatedCount) -gt 0) {
            $findings += [ordered]@{
                severity = 'critical'; code = 'voiceUsersInvalidEmergencyLocation'
                what = "$unknownCount user(s) reference an unknown location and $unvalidatedCount reference an unvalidated location."
                why = 'Unknown or unvalidated civic addresses may not satisfy emergency calling requirements.'
                fix = 'Validate the civic address in Teams, then use update-user-emergency-location to assign a validated location.'
            }
        }

        $result = Select-StagePage -Items $ordered -Pagination $pagination
        $after = [ordered]@{
            enterpriseVoiceUserCount = $ordered.Count
            coveredCount = $coveredCount
            missingCount = $missingCount
            unknownLocationCount = $unknownCount
            unvalidatedCount = $unvalidatedCount
            coveragePercent = if ($ordered.Count -eq 0) { 100 } else { [Math]::Round(100 * $coveredCount / $ordered.Count, 1) }
            users = @($result.Items)
            findings = $findings
        }
        return (Write-StageResult -Summary "Emergency coverage: $coveredCount of $($ordered.Count) enterprise-voice users are covered." -After $after -Page $result.Page)
    }
    default { throw "Tool 'report-emergency-coverage' does not implement stage '$Stage'." }
}