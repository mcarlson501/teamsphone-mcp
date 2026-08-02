BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsPhoneNumberAssignment { param([string]$TelephoneNumber, [string]$ErrorAction) }
    function Get-CsOnlineVoiceRoutingPolicy { param([string]$ErrorAction) }
    function Get-CsTenantDialPlan { param([string]$ErrorAction) }
    function Get-CsTeamsCallingPolicy { param([string]$ErrorAction) }
    function Set-CsPhoneNumberAssignment { param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [Nullable[bool]]$EnterpriseVoiceEnabled, [string]$LocationId, [string]$ErrorAction) }
    function Remove-CsPhoneNumberAssignment { param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [string]$ErrorAction) }
    function Grant-CsOnlineVoiceRoutingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTenantDialPlan { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTeamsCallingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Get-CsCallingLineIdentity { param([string]$ErrorAction) }
    function Grant-CsCallingLineIdentity { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Get-CsOnlineVoicemailUserSettings { param([string]$Identity, [string]$ErrorAction) }
    function Set-CsOnlineVoicemailUserSettings { param([string]$Identity, [Nullable[bool]]$IsEnabled, [Nullable[bool]]$OofGreetingFollowAutomaticRepliesEnabled, [Nullable[bool]]$OofGreetingFollowCalendarEnabled, [string]$ErrorAction) }
    function Get-CsOnlineLisLocation { param([Guid]$LocationId, [string]$ErrorAction) }

    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 40)
    }
    function Get-ToolInput { return @{ userUpn = 'new@contoso.com'; phoneNumber = '+15551234567' } }
    function Invoke-Snapshot {
        param([hashtable]$ToolInput = $null)
        if ($null -eq $ToolInput) { $ToolInput = Get-ToolInput }
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput $ToolInput) | ConvertFrom-Json -Depth 40)
    }
}

Describe 'onboard-voice-user' {
    BeforeEach {
        $global:ObEnterpriseVoice = $false
        $global:ObLineUri = $null
        $global:ObNumberStatus = 'Unassigned'
        $global:ObNumberTarget = $null
        $global:ObNumberLocation = '11111111-1111-1111-1111-111111111111'
        $global:ObCallerPolicy = $null
        $global:ObVoicemailEnabled = $true
        $global:ObFailCaller = $false

        Mock Get-CsOnlineUser {
            [PSCustomObject]@{
                UserPrincipalName = 'new@contoso.com'; Identity = 'new-id'; AccountType = 'User'
                EnterpriseVoiceEnabled = $global:ObEnterpriseVoice; LineUri = $global:ObLineUri
                FeatureTypes = @('MCOEV', 'MCOPSTN2'); CallingLineIdentity = if ($null -eq $global:ObCallerPolicy) { $null } else { [PSCustomObject]@{ Name = $global:ObCallerPolicy } }
            }
        }
        Mock Get-CsPhoneNumberAssignment {
            [PSCustomObject]@{ NumberType = 'CallingPlan'; PstnAssignmentStatus = $global:ObNumberStatus; AssignedPstnTargetId = $global:ObNumberTarget; LocationId = $global:ObNumberLocation }
        }
        Mock Set-CsPhoneNumberAssignment {
            if (-not [string]::IsNullOrWhiteSpace($Identity) -and -not [string]::IsNullOrWhiteSpace($PhoneNumber)) {
                $global:ObLineUri = "tel:$PhoneNumber"; $global:ObNumberStatus = 'UserAssigned'; $global:ObNumberTarget = 'new-id'
                if (-not [string]::IsNullOrWhiteSpace($LocationId)) { $global:ObNumberLocation = $LocationId }
            } elseif (-not [string]::IsNullOrWhiteSpace($PhoneNumber) -and -not [string]::IsNullOrWhiteSpace($LocationId)) { $global:ObNumberLocation = $LocationId }
            elseif ($null -ne $EnterpriseVoiceEnabled) { $global:ObEnterpriseVoice = $EnterpriseVoiceEnabled }
        }
        Mock Remove-CsPhoneNumberAssignment { $global:ObLineUri = $null; $global:ObNumberStatus = 'Unassigned'; $global:ObNumberTarget = $null }
        Mock Get-CsOnlineVoiceRoutingPolicy { @() }
        Mock Get-CsTenantDialPlan { @() }
        Mock Get-CsTeamsCallingPolicy { @() }
        Mock Grant-CsOnlineVoiceRoutingPolicy {}
        Mock Grant-CsTenantDialPlan {}
        Mock Grant-CsTeamsCallingPolicy {}
        Mock Get-CsCallingLineIdentity { @([PSCustomObject]@{ Identity = 'Tag:Mask-Service' }) }
        Mock Grant-CsCallingLineIdentity { if ($global:ObFailCaller) { throw 'Caller grant failed.' }; $global:ObCallerPolicy = $PolicyName }
        Mock Get-CsOnlineVoicemailUserSettings { [PSCustomObject]@{ IsEnabled = $global:ObVoicemailEnabled; OofGreetingFollowAutomaticRepliesEnabled = $false; OofGreetingFollowCalendarEnabled = $false } }
        Mock Set-CsOnlineVoicemailUserSettings { if ($null -ne $IsEnabled) { $global:ObVoicemailEnabled = $IsEnabled } }
        Mock Get-CsOnlineLisLocation { [PSCustomObject]@{ LocationId = $LocationId; IsValidated = $true } }
    }

    It 'captures atomic child snapshots' {
        $snapshot = Invoke-Snapshot
        $snapshot.children.assignPhoneNumber.snapshot.phoneNumber | Should -Be '+15551234567'
        $snapshot.children.PSObject.Properties.Name | Should -Contain 'assignPhoneNumber'
    }

    It 'returns four composite preflight checks when optional steps are omitted' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 40
        $parsed.checks | Should -HaveCount 4
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'aggregates child commands in dry run without writing' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 40
        $parsed.after.steps | Should -HaveCount 1
        $parsed.after.plannedCommands | Should -Not -BeNullOrEmpty
        Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'executes the core number and enterprise voice onboarding step' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 40
        $parsed.after.completedStepCount | Should -Be 1
        $global:ObLineUri | Should -Be 'tel:+15551234567'
        $global:ObEnterpriseVoice | Should -BeTrue
    }

    It 'executes optional caller ID, voicemail, and emergency steps' {
        $toolInput = Get-ToolInput
        $toolInput.callerIdPolicy = 'Mask-Service'
        $toolInput.voicemailEnabled = $false
        $toolInput.emergencyLocationId = '22222222-2222-2222-2222-222222222222'
        $snapshot = Invoke-Snapshot -ToolInput $toolInput

        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 40

        $parsed.after.completedStepCount | Should -Be 4
        $global:ObCallerPolicy | Should -Be 'Mask-Service'
        $global:ObVoicemailEnabled | Should -BeFalse
        $global:ObNumberLocation | Should -Be '22222222-2222-2222-2222-222222222222'
    }

    It 'compensates the completed number step when a later child fails' {
        $toolInput = Get-ToolInput
        $toolInput.callerIdPolicy = 'Mask-Service'
        $snapshot = Invoke-Snapshot -ToolInput $toolInput
        $global:ObFailCaller = $true

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot) } | Should -Throw '*Caller grant failed*'
        $global:ObLineUri | Should -BeNullOrEmpty
        $global:ObEnterpriseVoice | Should -BeFalse
    }

    It 'verifies all requested onboarding steps' {
        $toolInput = Get-ToolInput
        $toolInput.callerIdPolicy = 'Mask-Service'
        $snapshot = Invoke-Snapshot -ToolInput $toolInput
        $json = New-StageInput -ToolInput $toolInput -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 40
        $parsed.checks | Should -HaveCount 4
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rolls back completed onboarding state' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 40
        $parsed.after.restored | Should -BeTrue
        $global:ObLineUri | Should -BeNullOrEmpty
        $global:ObEnterpriseVoice | Should -BeFalse
    }
}