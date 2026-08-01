BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stub so Pester can mock a cmdlet that ships with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsOnlineLisLocation {
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
    function New-SampleLocations {
        @(
            [PSCustomObject]@{
                LocationId      = 'loc-2'
                CivicAddressId  = 'civic-1'
                Description     = 'Seattle HQ, floor 3'
                Location        = 'Floor 3'
                CompanyName     = 'Contoso'
                HouseNumber     = '123'
                StreetName      = 'Main'
                StreetSuffix    = 'Street'
                City            = 'Seattle'
                StateOrProvince = 'WA'
                PostalCode      = '98101'
                CountryOrRegion = 'US'
                IsValidated     = $true
            },
            [PSCustomObject]@{
                LocationId      = 'loc-1'
                CivicAddressId  = 'civic-1'
                Description     = 'Seattle HQ, floor 1'
                Location        = 'Floor 1'
                CompanyName     = 'Contoso'
                HouseNumber     = '123'
                StreetName      = 'Main'
                StreetSuffix    = 'Street'
                City            = 'Seattle'
                StateOrProvince = 'WA'
                PostalCode      = '98101'
                CountryOrRegion = 'US'
                IsValidated     = $true
            },
            [PSCustomObject]@{
                LocationId      = 'loc-3'
                CivicAddressId  = 'civic-2'
                Description     = 'London office'
                CompanyName     = 'Contoso'
                HouseNumber     = '8'
                StreetName      = 'Baker'
                StreetSuffix    = 'Street'
                City            = 'London'
                CountryOrRegion = 'GB'
                IsValidated     = $false
            }
        )
    }
}

Describe 'list-emergency-addresses' {
    Context 'execute stage' {
        It 'returns every location in stable order with page metadata' {
            Mock Get-CsOnlineLisLocation { New-SampleLocations }

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.after.totalMatched | Should -Be 3
            $parsed.after.validatedCount | Should -Be 2
            $parsed.after.unvalidatedCount | Should -Be 1
            $parsed.after.locations[0].countryOrRegion | Should -Be 'GB'
            $parsed.after.locations[0].streetAddress | Should -Be '8 Baker Street'
            $parsed.after.locations[1].locationId | Should -Be 'loc-1'
            $parsed.page.returnedCount | Should -Be 3
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'filters by city using a case-insensitive substring match' {
            Mock Get-CsOnlineLisLocation { New-SampleLocations }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ cityFilter = 'seatt' }) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 2
            $parsed.after.locations[0].city | Should -Be 'Seattle'
        }

        It 'honours the host-supplied page window' {
            Mock Get-CsOnlineLisLocation { New-SampleLocations }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -Pagination @{ pageSize = 2; offset = 0 }) | ConvertFrom-Json

            $parsed.after.locations | Should -HaveCount 2
            $parsed.page.hasMore | Should -BeTrue
            $parsed.page.nextOffset | Should -Be 2
        }

        It 'returns an empty page when the tenant has no locations' {
            Mock Get-CsOnlineLisLocation { @() }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 0
            $parsed.page.returnedCount | Should -Be 0
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'retries once when the tenant throttles the request' {
            $global:ThrottleAttempts = 0
            Mock Get-CsOnlineLisLocation {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                New-SampleLocations
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.totalMatched | Should -Be 3
        }

        It 'surfaces a terminating error when the read fails' {
            Mock Get-CsOnlineLisLocation { throw 'Access denied.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage snapshot -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
