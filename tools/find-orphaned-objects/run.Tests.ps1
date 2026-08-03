BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    function Get-CsOnlineApplicationInstance { param([string]$ErrorAction) }
    function Get-CsOnlineApplicationInstanceAssociation { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsOnlineUser { param([string]$Identity, [int]$ResultSize, [string]$ErrorAction) }
    function Get-CsAutoAttendant { param([string]$ErrorAction) }
    function Get-CsCallQueue { param([string]$ErrorAction) }
    function Get-CsOnlineVoiceRoutingPolicy { param([string]$ErrorAction) }
    function Get-CsTenantDialPlan { param([string]$ErrorAction) }
    function New-Input { @{ input = @{}; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5 }
}

Describe 'find-orphaned-objects' {
    BeforeEach {
        Mock Get-CsOnlineApplicationInstance {
            [PSCustomObject]@{
                ObjectId = 'ra-1'; DisplayName = 'Reception'; PhoneNumber = '+15550000001'
            }
        }
        Mock Get-CsOnlineApplicationInstanceAssociation {
            [PSCustomObject]@{ ConfigurationId = 'aa-1'; ConfigurationType = 'AutoAttendant' }
        }
        Mock Get-CsAutoAttendant { @() }
        Mock Get-CsCallQueue {
            [PSCustomObject]@{ Identity = 'cq-1'; Name = 'Support'; Agents = @([PSCustomObject]@{ ObjectId = 'agent-1' }) }
        }
        Mock Get-CsOnlineUser {
            if (-not [string]::IsNullOrWhiteSpace($Identity)) {
                return [PSCustomObject]@{ UserPrincipalName = 'reception@contoso.com'; FeatureTypes = @('PhoneSystem') }
            }
            return [PSCustomObject]@{
                UserPrincipalName = 'agent@contoso.com'; EnterpriseVoiceEnabled = $true; LineUri = 'tel:+15550000002'
                OnlineVoiceRoutingPolicy = [PSCustomObject]@{ Name = 'US-Routing' }
                TenantDialPlan = [PSCustomObject]@{ Name = 'US-DialPlan' }
            }
        }
        Mock Get-CsOnlineVoiceRoutingPolicy { [PSCustomObject]@{ Identity = 'Tag:US-Routing' } }
        Mock Get-CsTenantDialPlan { [PSCustomObject]@{ Identity = 'Tag:US-DialPlan' } }
    }

    It 'reports a clean tenant inventory as healthy' {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.status | Should -Be 'healthy'
        $parsed.after.findings | Should -HaveCount 0
    }

    It 'finds incomplete resource accounts' {
        Mock Get-CsOnlineApplicationInstance {
            [PSCustomObject]@{ ObjectId = 'ra-1'; DisplayName = 'Unused'; PhoneNumber = $null }
        }
        Mock Get-CsOnlineApplicationInstanceAssociation { throw 'Association was not found.' }
        Mock Get-CsOnlineUser {
            if (-not [string]::IsNullOrWhiteSpace($Identity)) { return [PSCustomObject]@{ UserPrincipalName = 'unused@contoso.com'; FeatureTypes = @('Teams') } }
            return @()
        }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.findings.code | Should -Contain 'resourceAccountWithoutNumber'
        $parsed.after.findings.code | Should -Contain 'unattachedResourceAccount'
        $parsed.after.findings.code | Should -Contain 'unlicensedResourceAccount'
    }

    It 'finds broken auto-attendant targets and empty queues' {
        Mock Get-CsAutoAttendant {
            [PSCustomObject]@{
                Name = 'Main'
                Operator = [PSCustomObject]@{ Id = 'deleted-ra'; Type = 'ApplicationEndpoint' }
                DefaultCallFlow = $null
                CallFlows = @()
            }
        }
        Mock Get-CsCallQueue { [PSCustomObject]@{ Identity = 'cq-empty'; Name = 'Empty'; Agents = @() } }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.findings.code | Should -Contain 'deletedApplicationTarget'
        $parsed.after.findings.code | Should -Contain 'emptyCallQueue'
    }

    It 'finds voice-enabled users without numbers and unused custom policies' {
        Mock Get-CsOnlineUser {
            if (-not [string]::IsNullOrWhiteSpace($Identity)) { return [PSCustomObject]@{ FeatureTypes = @('PhoneSystem') } }
            return [PSCustomObject]@{
                UserPrincipalName = 'orphan@contoso.com'; EnterpriseVoiceEnabled = $true; LineUri = $null
            }
        }
        Mock Get-CsOnlineVoiceRoutingPolicy {
            @([PSCustomObject]@{ Identity = 'Global' }, [PSCustomObject]@{ Identity = 'Tag:Unused-Routing' })
        }
        Mock Get-CsTenantDialPlan { [PSCustomObject]@{ Identity = 'Tag:Unused-DialPlan' } }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $parsed.after.findings.code | Should -Contain 'voiceEnabledUserWithoutNumber'
        @($parsed.after.findings | Where-Object { $_.code -eq 'unassignedCustomPolicy' }) | Should -HaveCount 2
    }

    It 'throws for an unsupported stage' {
        { & $script:RunScript -Stage verify -InputJson (New-Input) } | Should -Throw
    }
}