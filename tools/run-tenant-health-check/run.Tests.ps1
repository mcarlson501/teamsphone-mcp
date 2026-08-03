BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    function Get-CsPhoneNumberAssignment { param([string]$ErrorAction) }
    function Get-CsOnlineUser { param([string]$ResultSize, [string]$ErrorAction) }
    function Get-CsOnlineLisLocation { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstance { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstanceAssociation { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsCallQueue { param([string]$ErrorAction) }
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
            [PSCustomObject]@{ UserPrincipalName = 'user@contoso.com'; FeatureTypes = @('PhoneSystem'); EnterpriseVoiceEnabled = $true; LineUri = 'tel:+15550000001' }
        }
        Mock Get-CsOnlineLisLocation { @() }
        Mock Get-CsOnlineApplicationInstance {
            [PSCustomObject]@{ ObjectId = 'ra-1'; UserPrincipalName = 'ra@contoso.com'; ApplicationId = '11cd3e2e-fccb-42ad-ad00-878b93575e07' }
        }
        Mock Get-CsOnlineApplicationInstanceAssociation { throw 'Association was not found.' }
        Mock Get-CsCallQueue { [PSCustomObject]@{ Identity = 'cq-1'; Name = 'Support'; Agents = @() } }
    }

    It 'ranks seeded critical faults before warnings with remediation text' {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.overallStatus | Should -Be 'criticalIssues'
        $parsed.after.findingCounts.critical | Should -Be 2
        $parsed.after.findingCounts.warning | Should -Be 1
        $parsed.after.findings.code | Should -Contain 'voiceUsersMissingEmergencyLocation'
        $parsed.after.findings.code | Should -Contain 'callQueueHasNoAgents'
        $parsed.after.findings.code | Should -Contain 'unattachedResourceAccounts'
        $parsed.after.findings[0].severity | Should -Be 'critical'
        foreach ($finding in $parsed.after.findings) { $finding.fix | Should -Not -BeNullOrEmpty }
    }

    It 'degrades one failed section and returns findings from the others' {
        Mock Get-CsOnlineLisLocation { throw 'Emergency inventory denied.' }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.warnings.section | Should -Contain 'emergencyCoverage'
        $parsed.after.findings.code | Should -Contain 'callQueueHasNoAgents'
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