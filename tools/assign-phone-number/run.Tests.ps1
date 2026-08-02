BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsPhoneNumberAssignment { param([string]$TelephoneNumber, [string]$ErrorAction) }
    function Get-CsOnlineLisLocation { param([Guid]$LocationId, [string]$ErrorAction) }
    function Get-CsOnlineVoiceRoutingPolicy { param([string]$ErrorAction) }
    function Get-CsTenantDialPlan { param([string]$ErrorAction) }
    function Get-CsTeamsCallingPolicy { param([string]$ErrorAction) }
    function Set-CsPhoneNumberAssignment {
        param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [bool]$EnterpriseVoiceEnabled, [string]$LocationId, [string]$ErrorAction)
    }
    function Remove-CsPhoneNumberAssignment {
        param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [string]$ErrorAction)
    }
    function Grant-CsOnlineVoiceRoutingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTenantDialPlan { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTeamsCallingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }

    function New-TestUser {
        return [PSCustomObject]@{
            UserPrincipalName        = $global:ApnUserUpn
            Identity                 = "id-$($global:ApnUserUpn)"
            AccountType              = $global:ApnAccountType
            EnterpriseVoiceEnabled   = $global:ApnEnterpriseVoice
            LineUri                  = $global:ApnLineUri
            FeatureTypes             = $global:ApnFeatures
            OnlineVoiceRoutingPolicy = if ($null -eq $global:ApnVoiceRouting) { $null } else { [PSCustomObject]@{ Name = $global:ApnVoiceRouting } }
            TenantDialPlan           = if ($null -eq $global:ApnDialPlan) { $null } else { [PSCustomObject]@{ Name = $global:ApnDialPlan } }
            TeamsCallingPolicy       = if ($null -eq $global:ApnCalling) { $null } else { [PSCustomObject]@{ Name = $global:ApnCalling } }
        }
    }

    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 32)
    }

    function Get-DefaultToolInput {
        return @{
            userUpn                 = 'bob@contoso.com'
            phoneNumber             = '+15551234567'
            onlineVoiceRoutingPolicy = 'US-Voice'
            tenantDialPlan          = 'US-Dial'
            teamsCallingPolicy      = 'US-Calling'
            locationId              = '22222222-2222-2222-2222-222222222222'
        }
    }

    function Invoke-SnapshotStage {
        param([hashtable]$ToolInput = $null)
        if ($null -eq $ToolInput) { $ToolInput = Get-DefaultToolInput }
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput $ToolInput) | ConvertFrom-Json -Depth 32)
    }
}

Describe 'assign-phone-number' {
    BeforeEach {
        $global:ApnUserUpn = 'bob@contoso.com'
        $global:ApnNumber = '+15551234567'
        $global:ApnNumberType = 'CallingPlan'
        $global:ApnAssignmentStatus = 'Unassigned'
        $global:ApnAssignedTarget = $null
        $global:ApnLocationId = $null
        $global:ApnLocationValidated = $true
        $global:ApnAccountType = 'User'
        $global:ApnEnterpriseVoice = $false
        $global:ApnLineUri = $null
        $global:ApnFeatures = @('MCOEV', 'MCOPSTN2')
        $global:ApnVoiceRouting = $null
        $global:ApnDialPlan = $null
        $global:ApnCalling = $null
        $global:ApnFailCallingGrant = $false

        Mock Get-CsOnlineUser { New-TestUser }
        Mock Get-CsPhoneNumberAssignment {
            if ([string]::IsNullOrWhiteSpace($global:ApnNumberType)) { return $null }
            [PSCustomObject]@{
                TelephoneNumber      = $global:ApnNumber
                NumberType           = $global:ApnNumberType
                PstnAssignmentStatus = $global:ApnAssignmentStatus
                AssignedPstnTargetId = $global:ApnAssignedTarget
                LocationId = $global:ApnLocationId
            }
        }
        Mock Get-CsOnlineLisLocation {
            [PSCustomObject]@{ LocationId = $LocationId; IsValidated = $global:ApnLocationValidated }
        }
        Mock Get-CsOnlineVoiceRoutingPolicy { @([PSCustomObject]@{ Identity = 'Tag:US-Voice' }) }
        Mock Get-CsTenantDialPlan { @([PSCustomObject]@{ Identity = 'Tag:US-Dial' }) }
        Mock Get-CsTeamsCallingPolicy { @([PSCustomObject]@{ Identity = 'Tag:US-Calling' }) }
        Mock Set-CsPhoneNumberAssignment {
            if (-not [string]::IsNullOrWhiteSpace($Identity) -and -not [string]::IsNullOrWhiteSpace($PhoneNumber)) {
                $global:ApnLineUri = "tel:$PhoneNumber"
                $global:ApnAssignmentStatus = 'UserAssigned'
                $global:ApnAssignedTarget = "id-$Identity"
                if (-not [string]::IsNullOrWhiteSpace($LocationId)) { $global:ApnLocationId = $LocationId }
            } elseif (-not [string]::IsNullOrWhiteSpace($PhoneNumber) -and -not [string]::IsNullOrWhiteSpace($LocationId)) {
                $global:ApnLocationId = $LocationId
            } else {
                $global:ApnEnterpriseVoice = $EnterpriseVoiceEnabled
            }
        }
        Mock Remove-CsPhoneNumberAssignment {
            $global:ApnLineUri = $null
            $global:ApnAssignmentStatus = 'Unassigned'
            $global:ApnAssignedTarget = $null
            $global:ApnLocationId = $null
        }
        Mock Grant-CsOnlineVoiceRoutingPolicy { $global:ApnVoiceRouting = $PolicyName }
        Mock Grant-CsTenantDialPlan { $global:ApnDialPlan = $PolicyName }
        Mock Grant-CsTeamsCallingPolicy {
            if ($global:ApnFailCallingGrant) { throw 'Calling policy grant failed.' }
            $global:ApnCalling = $PolicyName
        }
    }

    Context 'snapshot stage' {
        It 'captures the user, unassigned number type, and requested policies' {
            $snapshot = Invoke-SnapshotStage

            $snapshot.phoneNumber | Should -Be '+15551234567'
            $snapshot.phoneNumberType | Should -Be 'CallingPlan'
            $snapshot.assignmentStatus | Should -Be 'Unassigned'
            $snapshot.user.userPrincipalName | Should -Be 'bob@contoso.com'
            $snapshot.requestedPolicies.onlineVoiceRoutingPolicy | Should -Be 'US-Voice'
            $snapshot.policyAvailability.teamsCallingPolicy | Should -BeTrue
        }

        It 'rejects an invalid E.164 number without writing' {
            $invalidInput = Get-DefaultToolInput
            $invalidInput.phoneNumber = 'invalid'

            { Invoke-SnapshotStage -ToolInput $invalidInput } | Should -Throw '*E.164*'
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }
    }

    Context 'preflight stage' {
        It 'passes all checks for a licensed user and unassigned number' {
            $snapshot = Invoke-SnapshotStage
            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            $parsed.checks | Should -HaveCount 8
            @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
        }

        It 'rejects a resource account' {
            $global:ApnAccountType = 'ResourceAccount'
            $snapshot = Invoke-SnapshotStage
            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'target is a user account' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'rejects a Calling Plan number without a matching license' {
            $global:ApnFeatures = @('MCOEV')
            $snapshot = Invoke-SnapshotStage
            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'target user is licensed for the number type' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'accepts the CallingPlan feature name reported by Teams' {
            $global:ApnFeatures = @('PhoneSystem', 'CallingPlan')
            $snapshot = Invoke-SnapshotStage

            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
        }

        It 'rejects a Calling Plan assignment without an existing or requested validated location' {
            $toolInput = Get-DefaultToolInput
            $toolInput.Remove('locationId')
            $snapshot = Invoke-SnapshotStage -ToolInput $toolInput

            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'Calling Plan number has a validated emergency location' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'rejects a number that is already assigned' {
            $global:ApnAssignmentStatus = 'UserAssigned'
            $global:ApnAssignedTarget = 'someone-else'
            $snapshot = Invoke-SnapshotStage
            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'the number is unassigned' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'rejects a requested policy that does not exist' {
            Mock Get-CsTeamsCallingPolicy { @() }
            $snapshot = Invoke-SnapshotStage
            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'requested policies exist' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'passes when the requested number is already assigned to the target' {
            $global:ApnLineUri = 'tel:+15551234567'
            $global:ApnAssignmentStatus = 'UserAssigned'
            $global:ApnAssignedTarget = 'id-bob@contoso.com'
            $global:ApnLocationId = '22222222-2222-2222-2222-222222222222'
            $snapshot = Invoke-SnapshotStage

            $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
        }
    }

    Context 'dryrun stage' {
        It 'renders the exact write sequence without invoking it' {
            $snapshot = Invoke-SnapshotStage
            $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            $parsed.after.plannedCommands | Should -HaveCount 5
            $parsed.after.plannedCommands[0] | Should -Match 'EnterpriseVoiceEnabled'
            $parsed.after.plannedCommands[1] | Should -Match 'Set-CsPhoneNumberAssignment.*PhoneNumber'
            $parsed.after.plannedCommands[4] | Should -Match 'Grant-CsTeamsCallingPolicy'
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Grant-CsTeamsCallingPolicy -Times 0 -Exactly
        }
    }

    Context 'execute stage' {
        It 'enables enterprise voice, assigns the number, and grants requested policies' {
            $snapshot = Invoke-SnapshotStage
            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter { $EnterpriseVoiceEnabled -eq $true }
            Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter { $PhoneNumber -eq '+15551234567' -and $PhoneNumberType -eq 'CallingPlan' }
            Should -Invoke Grant-CsOnlineVoiceRoutingPolicy -Times 1 -Exactly -ParameterFilter { $PolicyName -eq 'US-Voice' }
            Should -Invoke Grant-CsTenantDialPlan -Times 1 -Exactly -ParameterFilter { $PolicyName -eq 'US-Dial' }
            Should -Invoke Grant-CsTeamsCallingPolicy -Times 1 -Exactly -ParameterFilter { $PolicyName -eq 'US-Calling' }
            $parsed.after.changed | Should -BeTrue
            $parsed.after.lineUri | Should -Be '+15551234567'
        }

        It 'is idempotent when the requested state already exists' {
            $snapshot = Invoke-SnapshotStage
            $global:ApnEnterpriseVoice = $true
            $global:ApnLineUri = 'tel:+15551234567'
            $global:ApnAssignmentStatus = 'UserAssigned'
            $global:ApnAssignedTarget = 'id-bob@contoso.com'
            $global:ApnLocationId = '22222222-2222-2222-2222-222222222222'
            $global:ApnVoiceRouting = 'US-Voice'
            $global:ApnDialPlan = 'US-Dial'
            $global:ApnCalling = 'US-Calling'

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 32

            $parsed.after.changed | Should -BeFalse
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Grant-CsTeamsCallingPolicy -Times 0 -Exactly
        }

        It 'refuses to overwrite a number assigned after the snapshot' {
            $snapshot = Invoke-SnapshotStage
            $global:ApnLineUri = 'tel:+15559999999'

            { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) } | Should -Throw '*tenant state changed*'
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }

        It 'restores the original state when a later policy grant fails' {
            $snapshot = Invoke-SnapshotStage
            $global:ApnFailCallingGrant = $true

            { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot) } | Should -Throw '*Calling policy grant failed*'
            Should -Invoke Remove-CsPhoneNumberAssignment -Times 1 -Exactly
            $global:ApnLineUri | Should -BeNullOrEmpty
            $global:ApnEnterpriseVoice | Should -BeFalse
            $global:ApnVoiceRouting | Should -BeNullOrEmpty
            $global:ApnDialPlan | Should -BeNullOrEmpty
        }
    }

    Context 'verify and rollback stages' {
        It 'verifies the number, enterprise voice, and requested policies' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot
            $null = & $script:RunScript -Stage execute -InputJson $json

            $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.checks | Should -HaveCount 3
            @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
        }

        It 'restores the snapshot when rollback is requested' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot
            $null = & $script:RunScript -Stage execute -InputJson $json

            $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.lineUri | Should -BeNullOrEmpty
            $parsed.enterpriseVoiceEnabled | Should -BeFalse
            Should -Invoke Remove-CsPhoneNumberAssignment -Times 1 -Exactly
        }
    }

    It 'rejects an unsupported stage' {
        { & $script:RunScript -Stage verify -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput)) } | Should -Throw '*snapshot*'
    }
}