BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    function Get-CsPhoneNumberAssignment { param([string]$ErrorAction) }
    function Get-CsOnlineUser { param([string]$Identity, [string]$ResultSize, [string]$ErrorAction) }
    function Get-CsOnlineLisLocation { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstance { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstanceAssociation { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsAutoAttendant { param([string]$ErrorAction) }
    function Get-CsCallQueue { param([string]$Identity, [string]$NameFilter, [string]$ErrorAction) }
    function Get-CsOnlineVoiceRoutingPolicy { param([string]$ErrorAction) }
    function Get-CsTenantDialPlan { param([string]$ErrorAction) }
    function New-Input {
        @{ input = @{ tenantId = 'tenant'; credentialRef = 'cred' }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5
    }
}

Describe 'run-tenant-health-check' {
    BeforeEach {
        Mock Get-CsPhoneNumberAssignment {
            @(
                [PSCustomObject]@{ TelephoneNumber = '+15550000001'; NumberType = 'CallingPlan'; IsoCountryCode = 'US'; PstnAssignmentStatus = 'UserAssigned'; LocationId = $null },
                [PSCustomObject]@{ TelephoneNumber = '+15550000002'; NumberType = 'CallingPlan'; IsoCountryCode = 'US'; PstnAssignmentStatus = 'Unassigned' }
            )
        }
        Mock Get-CsOnlineUser {
            if (-not [string]::IsNullOrWhiteSpace($Identity)) {
                return [PSCustomObject]@{ UserPrincipalName = 'resource@contoso.com'; FeatureTypes = @('PhoneSystem') }
            }
            [PSCustomObject]@{ UserPrincipalName = 'user@contoso.com'; FeatureTypes = @('PhoneSystem'); EnterpriseVoiceEnabled = $true; LineUri = 'tel:+15550000001' }
        }
        Mock Get-CsOnlineLisLocation { @() }
        Mock Get-CsOnlineApplicationInstance {
            [PSCustomObject]@{ ObjectId = 'ra-1'; DisplayName = 'Reception'; UserPrincipalName = 'ra@contoso.com'; PhoneNumber = '+15550000003'; ApplicationId = '11cd3e2e-fccb-42ad-ad00-878b93575e07' }
        }
        Mock Get-CsOnlineApplicationInstanceAssociation { throw 'Association was not found.' }
        Mock Get-CsAutoAttendant { @() }
        Mock Get-CsCallQueue { [PSCustomObject]@{ Identity = '22222222-2222-2222-2222-222222222222'; Name = 'Support'; Agents = @() } }
        Mock Get-CsOnlineVoiceRoutingPolicy { @() }
        Mock Get-CsTenantDialPlan { @() }
    }

    It 'ranks seeded critical faults before warnings with remediation text' {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.overallStatus | Should -Be 'criticalIssues'
        $parsed.after.findingCounts.critical | Should -Be 2
        $parsed.after.findingCounts.warning | Should -Be 1
        $parsed.after.findings.code | Should -Contain 'voiceUsersMissingEmergencyLocation'
        $parsed.after.findings.code | Should -Contain 'noConfiguredAgents'
        $parsed.after.findings.code | Should -Contain 'unattachedResourceAccount'
        $parsed.after.findings[0].severity | Should -Be 'critical'
        foreach ($finding in $parsed.after.findings) { $finding.fix | Should -Not -BeNullOrEmpty }
    }

    It 'degrades one failed section and returns findings from the others' {
        Mock Get-CsOnlineLisLocation { throw 'Emergency inventory denied.' }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.warnings.section | Should -Contain 'emergencyCoverage'
        $parsed.after.findings.code | Should -Contain 'noConfiguredAgents'
    }

    It 'does not call a partially evaluated tenant healthy' {
        Mock Get-CsPhoneNumberAssignment { @() }
        Mock Get-CsOnlineUser { @() }
        Mock Get-CsOnlineLisLocation { throw 'Emergency inventory denied.' }
        Mock Get-CsOnlineApplicationInstance { @() }
        Mock Get-CsCallQueue { @() }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.overallStatus | Should -Be 'degraded'
        $parsed.after.findings | Should -HaveCount 0
    }
}