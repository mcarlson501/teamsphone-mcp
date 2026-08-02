BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsPhoneNumberAssignment { param([string]$TelephoneNumber, [string]$ErrorAction) }
    function Get-CsCallQueue { param([string]$Identity, [string]$ErrorAction) }
    function Set-CsCallQueue { param([string]$Identity, [string[]]$Users, [string]$ErrorAction) }
    function Remove-CsPhoneNumberAssignment { param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [string]$ErrorAction) }
    function Set-CsPhoneNumberAssignment { param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [Nullable[bool]]$EnterpriseVoiceEnabled, [string]$ErrorAction) }
    function Grant-CsOnlineVoiceRoutingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTenantDialPlan { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTeamsCallingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsCallingLineIdentity { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }

    function New-StageInput {
        param([object]$Snapshot = $null)
        return (@{ input = @{ userUpn = 'former@contoso.com' }; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 40)
    }
    function Invoke-Snapshot {
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput) | ConvertFrom-Json -Depth 40)
    }
}

Describe 'offboard-voice-user' {
    BeforeEach {
        $global:OffEnterpriseVoice = $true
        $global:OffLineUri = 'tel:+15551234567'
        $global:OffNumberStatus = 'UserAssigned'
        $global:OffNumberTarget = 'former-id'
        $global:OffPolicies = @{ routing = 'US-Route'; dial = 'US-Dial'; calling = 'Standard'; caller = 'Mask-Service' }
        $global:OffQueues = @{
            'queue-1' = @('former-id', 'other-id')
            'queue-2' = @('other-id')
        }
        $global:OffFailPolicy = $false

        Mock Get-CsOnlineUser {
            [PSCustomObject]@{
                UserPrincipalName = 'former@contoso.com'; Identity = 'former-id'; AccountType = 'User'
                EnterpriseVoiceEnabled = $global:OffEnterpriseVoice; LineUri = $global:OffLineUri
                OnlineVoiceRoutingPolicy = $global:OffPolicies.routing; TenantDialPlan = $global:OffPolicies.dial
                TeamsCallingPolicy = $global:OffPolicies.calling; CallingLineIdentity = $global:OffPolicies.caller
            }
        }
        Mock Get-CsPhoneNumberAssignment {
            [PSCustomObject]@{ NumberType = 'CallingPlan'; PstnAssignmentStatus = $global:OffNumberStatus; AssignedPstnTargetId = $global:OffNumberTarget }
        }
        Mock Get-CsCallQueue {
            $ids = if ([string]::IsNullOrWhiteSpace($Identity)) { @($global:OffQueues.Keys) } else { @($Identity) }
            foreach ($id in $ids) {
                [PSCustomObject]@{ Identity = $id; Name = "Queue $id"; Agents = @($global:OffQueues[$id] | ForEach-Object { [PSCustomObject]@{ ObjectId = $_ } }); DistributionLists = @('distribution-list-id') }
            }
        }
        Mock Set-CsCallQueue { $global:OffQueues[$Identity] = @($Users) }
        Mock Remove-CsPhoneNumberAssignment { $global:OffLineUri = $null; $global:OffNumberStatus = 'Unassigned'; $global:OffNumberTarget = $null }
        Mock Set-CsPhoneNumberAssignment {
            if ($null -ne $EnterpriseVoiceEnabled) { $global:OffEnterpriseVoice = $EnterpriseVoiceEnabled }
            elseif (-not [string]::IsNullOrWhiteSpace($PhoneNumber)) { $global:OffLineUri = "tel:$PhoneNumber"; $global:OffNumberStatus = 'UserAssigned'; $global:OffNumberTarget = 'former-id' }
        }
        Mock Grant-CsOnlineVoiceRoutingPolicy { if ($global:OffFailPolicy) { throw 'Policy cleanup failed.' }; $global:OffPolicies.routing = $PolicyName }
        Mock Grant-CsTenantDialPlan { $global:OffPolicies.dial = $PolicyName }
        Mock Grant-CsTeamsCallingPolicy { $global:OffPolicies.calling = $PolicyName }
        Mock Grant-CsCallingLineIdentity { $global:OffPolicies.caller = $PolicyName }
    }

    It 'captures direct queue memberships, number, enterprise voice, and policies' {
        $snapshot = Invoke-Snapshot
        $snapshot.queueMemberships | Should -HaveCount 1
        $snapshot.numberSnapshot.phoneNumber | Should -Be '+15551234567'
        $snapshot.user.enterpriseVoiceEnabled | Should -BeTrue
        $snapshot.user.callerIdPolicy | Should -Be 'Mask-Service'
    }

    It 'passes four offboarding preflight checks' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 40
        $parsed.checks | Should -HaveCount 4
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'plans queue, number, policy, and enterprise voice changes without writing' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 40
        $parsed.after.plannedCommands.Count | Should -BeGreaterThan 6
        Should -Invoke Set-CsCallQueue -Times 0 -Exactly
        Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'offboards the user and returns a disposition report' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 40
        $global:OffQueues['queue-1'] | Should -Not -Contain 'former-id'
        $global:OffLineUri | Should -BeNullOrEmpty
        $global:OffEnterpriseVoice | Should -BeFalse
        $global:OffPolicies.Values | Should -Not -Contain 'US-Route'
        $parsed.after.disposition.numberDisposition | Should -Be 'releasedToTenantInventory'
        $parsed.after.disposition.removedQueueNames | Should -Contain 'Queue queue-1'
    }

    It 'is idempotent when no voice state remains' {
        $global:OffEnterpriseVoice = $false
        $global:OffLineUri = $null
        $global:OffNumberStatus = 'Unassigned'
        $global:OffNumberTarget = $null
        $global:OffPolicies = @{ routing = $null; dial = $null; calling = $null; caller = $null }
        $global:OffQueues['queue-1'] = @('other-id')
        $snapshot = Invoke-Snapshot

        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 40

        $parsed.after.disposition.numberDisposition | Should -Be 'notAssigned'
        $parsed.after.disposition.removedQueueNames | Should -HaveCount 0
    }

    It 'compensates queue and number changes when policy cleanup fails' {
        $snapshot = Invoke-Snapshot
        $global:OffFailPolicy = $true

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -Snapshot $snapshot) } | Should -Throw '*Policy cleanup failed*'
        $global:OffQueues['queue-1'] | Should -Contain 'former-id'
        $global:OffLineUri | Should -Be 'tel:+15551234567'
    }

    It 'verifies the complete offboarding disposition' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 40
        $parsed.checks | Should -HaveCount 4
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'restores all captured voice state' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 40
        $parsed.after.restored | Should -BeTrue
        $global:OffQueues['queue-1'] | Should -Contain 'former-id'
        $global:OffLineUri | Should -Be 'tel:+15551234567'
        $global:OffEnterpriseVoice | Should -BeTrue
        $global:OffPolicies.routing | Should -Be 'US-Route'
    }
}