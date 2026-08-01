BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stubs so Pester can mock cmdlets that ship with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsOnlineApplicationInstance { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstanceAssociation { param([string]$Identity, [string]$ErrorAction) }

    function New-StageInput {
        param(
            [hashtable]$ToolInput = @{},
            $Pagination = $null
        )

        return (@{ input = $ToolInput; snapshot = $null; pagination = $Pagination } | ConvertTo-Json -Depth 5)
    }

    # Mock bodies execute in the invoked script's scope, so fixture data is
    # exposed through a function rather than a $script: variable.
    function New-SampleInstances {
        @(
            [PSCustomObject]@{
                ObjectId          = 'ra-2'
                UserPrincipalName = 'ra-support@contoso.com'
                DisplayName       = 'Support Queue RA'
                ApplicationId     = '11CD3E2E-FCCB-42AD-AD00-878B93575E07'
                PhoneNumber       = 'tel:+15551110002'
            },
            [PSCustomObject]@{
                ObjectId          = 'ra-1'
                UserPrincipalName = 'ra-reception@contoso.com'
                DisplayName       = 'Main Reception RA'
                ApplicationId     = 'ce933385-9390-45d1-9512-c8d228074e07'
                PhoneNumber       = '+15551110001'
            },
            [PSCustomObject]@{
                ObjectId          = 'ra-3'
                UserPrincipalName = 'ra-spare@contoso.com'
                DisplayName       = 'Spare RA'
                ApplicationId     = '11cd3e2e-fccb-42ad-ad00-878b93575e07'
                PhoneNumber       = $null
            }
        )
    }
}

Describe 'list-resource-accounts' {
    Context 'execute stage' {
        It 'returns accounts in stable order with resolved associations' {
            Mock Get-CsOnlineApplicationInstance { New-SampleInstances }
            Mock Get-CsOnlineApplicationInstanceAssociation {
                if ($Identity -eq 'ra-3') { throw 'The application instance is not associated.' }
                [PSCustomObject]@{ ConfigurationId = "config-$Identity"; ConfigurationType = 'CallQueue' }
            }

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.after.totalMatched | Should -Be 3
            $parsed.after.resourceAccounts[0].userPrincipalName | Should -Be 'ra-reception@contoso.com'
            $parsed.after.resourceAccounts[0].applicationType | Should -Be 'autoAttendant'
            $parsed.after.resourceAccounts[0].phoneNumber | Should -Be '+15551110001'
            $parsed.after.resourceAccounts[1].userPrincipalName | Should -Be 'ra-spare@contoso.com'
            $parsed.after.resourceAccounts[1].applicationType | Should -Be 'callQueue'
            $parsed.after.resourceAccounts[1].phoneNumber | Should -BeNullOrEmpty
            $parsed.after.resourceAccounts[1].attached | Should -BeFalse
            $parsed.after.resourceAccounts[1].association | Should -BeNullOrEmpty
            $parsed.after.resourceAccounts[2].phoneNumber | Should -Be '+15551110002'
            $parsed.after.resourceAccounts[2].attached | Should -BeTrue
            $parsed.after.attachedOnThisPage | Should -Be 2
            $parsed.after.unattachedOnThisPage | Should -Be 1
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'filters by application type' {
            Mock Get-CsOnlineApplicationInstance { New-SampleInstances }
            Mock Get-CsOnlineApplicationInstanceAssociation { [PSCustomObject]@{ ConfigurationId = 'c'; ConfigurationType = 'AutoAttendant' } }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput @{ applicationType = 'autoAttendant' }) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 1
            $parsed.after.resourceAccounts[0].applicationType | Should -Be 'autoAttendant'
        }

        It 'resolves associations for the returned page only' {
            Mock Get-CsOnlineApplicationInstance { New-SampleInstances }
            Mock Get-CsOnlineApplicationInstanceAssociation { [PSCustomObject]@{ ConfigurationId = 'c'; ConfigurationType = 'CallQueue' } }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -Pagination @{ pageSize = 1; offset = 0 }) | ConvertFrom-Json

            $parsed.after.resourceAccounts | Should -HaveCount 1
            $parsed.page.hasMore | Should -BeTrue
            $parsed.page.nextOffset | Should -Be 1
            Should -Invoke Get-CsOnlineApplicationInstanceAssociation -Times 1 -Exactly
        }

        It 'returns an empty page when the tenant has no resource accounts' {
            Mock Get-CsOnlineApplicationInstance { @() }
            Mock Get-CsOnlineApplicationInstanceAssociation { throw 'should not be called' }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.totalMatched | Should -Be 0
            $parsed.page.returnedCount | Should -Be 0
            $parsed.page.hasMore | Should -BeFalse
        }

        It 'retries once when the tenant throttles the request' {
            $global:ThrottleAttempts = 0
            Mock Get-CsOnlineApplicationInstance {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                New-SampleInstances
            }
            Mock Get-CsOnlineApplicationInstanceAssociation { [PSCustomObject]@{ ConfigurationId = 'c'; ConfigurationType = 'CallQueue' } }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.totalMatched | Should -Be 3
        }

        It 'surfaces a terminating error when the inventory read fails' {
            Mock Get-CsOnlineApplicationInstance { throw 'Access denied.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage verify -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
