BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stubs so Pester can mock cmdlets that ship with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsPhoneNumberAssignment { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstance { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstanceAssociation { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsOnlineLisLocation { param([string]$ErrorAction) }
    function Get-CsOnlineSchedule { param([string]$ErrorAction) }
    function Get-CsOnlineVoiceRoutingPolicy { param([string]$ErrorAction) }
    function Get-CsTenantDialPlan { param([string]$ErrorAction) }
    function Get-CsTeamsCallingPolicy { param([string]$ErrorAction) }
    function Get-CsOnlineVoicemailPolicy { param([string]$ErrorAction) }
    function Get-CsCallQueue { param([string]$ErrorAction) }
    function Get-CsAutoAttendant { param([string]$ErrorAction) }

    function New-StageInput {
        return (@{
            input      = @{ tenantId = 'contoso.onmicrosoft.com'; credentialRef = 'contoso' }
            snapshot   = $null
            pagination = $null
        } | ConvertTo-Json -Depth 5)
    }

    function Set-HealthyTenantMocks {
        Mock Get-CsPhoneNumberAssignment {
            @(
                [PSCustomObject]@{ TelephoneNumber = '+15551110001'; NumberType = 'CallingPlan'; PstnAssignmentStatus = 'UserAssigned'; AssignedPstnTargetId = 'user-1' },
                [PSCustomObject]@{ TelephoneNumber = '+15551110002'; NumberType = 'OperatorConnect'; PstnAssignmentStatus = 'Unassigned' },
                [PSCustomObject]@{ TelephoneNumber = '+15551110003'; NumberType = 'CallingPlan'; PstnAssignmentStatus = 'Unassigned' }
            )
        }
        Mock Get-CsOnlineApplicationInstance {
            @(
                [PSCustomObject]@{ ObjectId = 'ra-1'; UserPrincipalName = 'ra-reception@contoso.com'; DisplayName = 'Reception'; ApplicationId = 'ce933385-9390-45d1-9512-c8d228074e07' },
                [PSCustomObject]@{ ObjectId = 'ra-2'; UserPrincipalName = 'ra-support@contoso.com'; DisplayName = 'Support'; ApplicationId = '11cd3e2e-fccb-42ad-ad00-878b93575e07' }
            )
        }
        Mock Get-CsOnlineApplicationInstanceAssociation {
            # The real cmdlet throws when a resource account has no association.
            if ($Identity -ne 'ra-1') { throw "Association for $Identity was not found." }
            [PSCustomObject]@{ Id = 'ra-1'; ConfigurationId = 'aa-1'; ConfigurationType = 'AutoAttendant' }
        }
        Mock Get-CsOnlineLisLocation {
            @(
                [PSCustomObject]@{ LocationId = 'loc-1'; City = 'Redmond'; CountryOrRegion = 'US'; IsValidated = $true },
                [PSCustomObject]@{ LocationId = 'loc-2'; City = 'Seattle'; CountryOrRegion = 'US'; IsValidated = $false }
            )
        }
        Mock Get-CsOnlineSchedule { @([PSCustomObject]@{ Id = 'sched-1'; Name = 'Holidays'; Type = 'Fixed' }) }
        Mock Get-CsOnlineVoiceRoutingPolicy { @([PSCustomObject]@{ Identity = 'Tag:US' }, [PSCustomObject]@{ Identity = 'Global' }) }
        Mock Get-CsTenantDialPlan { @([PSCustomObject]@{ Identity = 'Tag:Redmond' }) }
        Mock Get-CsTeamsCallingPolicy { @([PSCustomObject]@{ Identity = 'Global' }) }
        Mock Get-CsOnlineVoicemailPolicy { @([PSCustomObject]@{ Identity = 'Global' }) }
        Mock Get-CsCallQueue { @([PSCustomObject]@{ Identity = 'cq-1' }, [PSCustomObject]@{ Identity = 'cq-2' }) }
        Mock Get-CsAutoAttendant { @([PSCustomObject]@{ Identity = 'aa-1'; Name = 'Main'; CallHandlingAssociations = @() }) }
    }
}

Describe 'get-tenant-voice-snapshot' {
    Context 'execute stage' {
        It 'aggregates every voice estate section from the sibling read tools' {
            Set-HealthyTenantMocks

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.after.phoneNumbers.total | Should -Be 3
            $parsed.after.phoneNumbers.assigned | Should -Be 1
            $parsed.after.phoneNumbers.unassigned | Should -Be 2
            $parsed.after.phoneNumbers.byType.CallingPlan | Should -Be 2
            $parsed.after.phoneNumbers.byType.OperatorConnect | Should -Be 1
            $parsed.after.resourceAccounts.total | Should -Be 2
            $parsed.after.resourceAccounts.attached | Should -Be 1
            $parsed.after.resourceAccounts.byApplicationType.autoAttendant | Should -Be 1
            $parsed.after.emergencyAddresses.total | Should -Be 2
            $parsed.after.emergencyAddresses.validated | Should -Be 1
            $parsed.after.schedules.total | Should -Be 1
            $parsed.after.policies.voiceRoutingPolicies | Should -Be 2
            $parsed.after.policies.tenantDialPlans | Should -Be 1
            $parsed.after.callQueues.total | Should -Be 2
            $parsed.after.autoAttendants.total | Should -Be 1
            $parsed.after.warnings | Should -HaveCount 0
        }

        It 'does not emit pagination because the snapshot is a single aggregate' {
            Set-HealthyTenantMocks

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.PSObject.Properties.Name | Should -Not -Contain 'page'
            $parsed.summary | Should -Match 'Tenant voice snapshot'
        }

        It 'degrades a failing section to a warning and still returns the rest' {
            Set-HealthyTenantMocks
            Mock Get-CsOnlineLisLocation { throw 'Access denied reading emergency locations.' }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.emergencyAddresses | Should -BeNullOrEmpty
            $parsed.after.warnings | Should -HaveCount 1
            $parsed.after.warnings[0].section | Should -Be 'emergencyAddresses'
            $parsed.after.warnings[0].message | Should -Match 'Access denied'
            $parsed.after.phoneNumbers.total | Should -Be 3
            $parsed.after.callQueues.total | Should -Be 2
        }

        It 'reports empty counts for a tenant with no voice configuration' {
            Mock Get-CsPhoneNumberAssignment { @() }
            Mock Get-CsOnlineApplicationInstance { @() }
            Mock Get-CsOnlineLisLocation { @() }
            Mock Get-CsOnlineSchedule { @() }
            Mock Get-CsOnlineVoiceRoutingPolicy { @() }
            Mock Get-CsTenantDialPlan { @() }
            Mock Get-CsTeamsCallingPolicy { @() }
            Mock Get-CsOnlineVoicemailPolicy { @() }
            Mock Get-CsCallQueue { @() }
            Mock Get-CsAutoAttendant { @() }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $parsed.after.phoneNumbers.total | Should -Be 0
            $parsed.after.resourceAccounts.total | Should -Be 0
            $parsed.after.callQueues.total | Should -Be 0
            $parsed.after.warnings | Should -HaveCount 0
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage dryrun -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
