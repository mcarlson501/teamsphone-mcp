BeforeAll {
    $modulePath = Join-Path $PSScriptRoot 'TeamsPhoneMcp.Common.psm1'
    Import-Module $modulePath -Force -DisableNameChecking
}

Describe 'TeamsPhoneMcp.Common' {
    It 'writes pagination metadata in the single stage result' {
        $page = [ordered]@{
            returnedCount = 75
            hasMore       = $true
            nextOffset    = 175
        }

        $result = Write-StageResult -Summary 'Retrieved a page.' -After @{ items = @() } -Page $page

        $result | Should -HaveCount 1
        $parsed = $result | ConvertFrom-Json
        $parsed.page.returnedCount | Should -Be 75
        $parsed.page.hasMore | Should -BeTrue
        $parsed.page.nextOffset | Should -Be 175
    }

    Context 'Get-PropertyValue' {
        It 'returns the default when the property is absent' {
            $value = Get-PropertyValue -InputObject ([pscustomobject]@{ Present = 'yes' }) -Name 'Missing' -Default 'fallback'
            $value | Should -Be 'fallback'
        }

        It 'returns the default for a null input object' {
            Get-PropertyValue -InputObject $null -Name 'Anything' | Should -BeNullOrEmpty
        }

        It 'reads a hashtable key' {
            Get-PropertyValue -InputObject @{ pageSize = 25 } -Name 'pageSize' | Should -Be 25
        }
    }

    Context 'Test-IsGuid' {
        It 'recognizes a GUID' {
            Test-IsGuid -Value '3f2504e0-4f89-11d3-9a0c-0305e82c3301' | Should -BeTrue
        }

        It 'rejects a friendly name' {
            Test-IsGuid -Value 'Sales Queue' | Should -BeFalse
        }

        It 'rejects an empty value' {
            Test-IsGuid -Value '' | Should -BeFalse
        }
    }

    Context 'Get-AssignedPolicyName' {
        It 'passes a string assignment through' {
            Get-AssignedPolicyName -Value 'US-NY' | Should -Be 'US-NY'
        }

        It 'reads the Name property of an object assignment' {
            Get-AssignedPolicyName -Value ([pscustomobject]@{ Name = 'US-Unrestricted' }) | Should -Be 'US-Unrestricted'
        }

        It 'returns null for an unassigned policy' {
            Get-AssignedPolicyName -Value $null | Should -BeNullOrEmpty
        }
    }

    Context 'ConvertTo-E164Number' {
        It 'normalizes a tel URI with an extension' {
            ConvertTo-E164Number -Value 'tel:+1 (555) 123-4567;ext=2100' | Should -Be '+15551234567'
        }

        It 'returns null for a non-E.164 value' {
            ConvertTo-E164Number -Value '5551234567' | Should -BeNullOrEmpty
        }

        It 'returns null for an empty value' {
            ConvertTo-E164Number -Value '' | Should -BeNullOrEmpty
        }
    }

    Context 'Select-StagePage' {
        It 'slices the requested window and reports the next offset' {
            $result = Select-StagePage -Items (1..10) -Pagination ([pscustomobject]@{ pageSize = 3; offset = 3 })

            $result.Items | Should -Be @(4, 5, 6)
            $result.TotalCount | Should -Be 10
            $result.Page.returnedCount | Should -Be 3
            $result.Page.hasMore | Should -BeTrue
            $result.Page.nextOffset | Should -Be 6
        }

        It 'reports the final page without a next offset' {
            $result = Select-StagePage -Items (1..5) -Pagination ([pscustomobject]@{ pageSize = 10; offset = 0 })

            $result.Page.returnedCount | Should -Be 5
            $result.Page.hasMore | Should -BeFalse
            $result.Page.Contains('nextOffset') | Should -BeFalse
        }

        It 'returns an empty page when the offset is past the end' {
            $result = Select-StagePage -Items (1..3) -Pagination ([pscustomobject]@{ pageSize = 5; offset = 10 })

            $result.Items | Should -HaveCount 0
            $result.Page.returnedCount | Should -Be 0
            $result.Page.hasMore | Should -BeFalse
        }

        It 'returns every item when pagination is not requested' {
            $result = Select-StagePage -Items (1..4) -Pagination $null

            $result.Items | Should -HaveCount 4
            $result.Page.hasMore | Should -BeFalse
        }

        It 'handles an empty collection' {
            $result = Select-StagePage -Items @() -Pagination ([pscustomobject]@{ pageSize = 10; offset = 0 })

            $result.TotalCount | Should -Be 0
            $result.Page.returnedCount | Should -Be 0
            $result.Page.hasMore | Should -BeFalse
        }
    }
}