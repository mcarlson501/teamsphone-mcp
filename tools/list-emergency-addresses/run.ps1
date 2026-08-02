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

function Format-StreetAddress {
    param($Location)

    # The whole pipeline is wrapped so a fully empty address stays an array
    # instead of collapsing to $null under StrictMode.
    $parts = @(
        @(
            Get-PropertyValue -InputObject $Location -Name 'HouseNumber'
            Get-PropertyValue -InputObject $Location -Name 'HouseNumberSuffix'
            Get-PropertyValue -InputObject $Location -Name 'PreDirectional'
            Get-PropertyValue -InputObject $Location -Name 'StreetName'
            Get-PropertyValue -InputObject $Location -Name 'StreetSuffix'
            Get-PropertyValue -InputObject $Location -Name 'PostDirectional'
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { [string]$_ }
    )

    if ($parts.Count -eq 0) { return $null }
    return ($parts -join ' ')
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$pagination = Get-PropertyValue -InputObject $payload -Name 'pagination'
$cityFilter = [string](Get-PropertyValue -InputObject $toolInput -Name 'cityFilter' -Default '')

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        $locations = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineLisLocation -ErrorAction Stop })

        $matched = @()
        foreach ($location in $locations) {
            $city = [string](Get-PropertyValue -InputObject $location -Name 'City' -Default '')
            if (-not [string]::IsNullOrWhiteSpace($cityFilter) -and
                $city.IndexOf($cityFilter, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }

            $matched += [ordered]@{
                locationId      = [string](Get-PropertyValue -InputObject $location -Name 'LocationId')
                civicAddressId  = [string](Get-PropertyValue -InputObject $location -Name 'CivicAddressId')
                description     = [string](Get-PropertyValue -InputObject $location -Name 'Description')
                placeName       = [string](Get-PropertyValue -InputObject $location -Name 'Location')
                companyName     = [string](Get-PropertyValue -InputObject $location -Name 'CompanyName')
                streetAddress   = Format-StreetAddress -Location $location
                city            = if ([string]::IsNullOrWhiteSpace($city)) { $null } else { $city }
                stateOrProvince = [string](Get-PropertyValue -InputObject $location -Name 'StateOrProvince')
                postalCode      = [string](Get-PropertyValue -InputObject $location -Name 'PostalCode')
                countryOrRegion = [string](Get-PropertyValue -InputObject $location -Name 'CountryOrRegion')
                validated       = Test-TeamsEmergencyLocationValidated -Location $location
                elin            = [string](Get-PropertyValue -InputObject $location -Name 'Elin')
            }
        }

        # Deterministic ordering keeps a continuation token pointing at the same
        # window across calls.
        $ordered = @($matched | Sort-Object -Property { "$($_.countryOrRegion)|$($_.city)|$($_.streetAddress)|$($_.locationId)" })
        $validatedCount = @($ordered | Where-Object { $_.validated }).Count

        $result = Select-StagePage -Items $ordered -Pagination $pagination

        $after = [ordered]@{
            totalMatched     = $result.TotalCount
            validatedCount   = $validatedCount
            unvalidatedCount = $result.TotalCount - $validatedCount
            locations        = @($result.Items)
        }

        $summary = "Matched $($result.TotalCount) emergency locations ($validatedCount validated); returning $($result.Page.returnedCount)."
        return (Write-StageResult -Summary $summary -After $after -Page $result.Page)
    }
    default {
        throw "Tool 'list-emergency-addresses' does not implement stage '$Stage'."
    }
}
