BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    # Stubs so Pester can mock cmdlets that ship with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsOnlineUser {
        param([string]$Identity, [string]$ErrorAction)
    }

    function Get-CsPhoneNumberAssignment {
        param([string]$TelephoneNumber, [string]$ErrorAction)
    }

    function Remove-CsPhoneNumberAssignment {
        param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [string]$ErrorAction)
    }

    function Set-CsPhoneNumberAssignment {
        param(
            [string]$Identity,
            [string]$PhoneNumber,
            [string]$PhoneNumberType,
            [bool]$EnterpriseVoiceEnabled,
            [string]$ErrorAction)
    }

    function New-TestUser {
        param(
            [string]$Upn,
            [AllowNull()][string]$LineUri,
            [bool]$EnterpriseVoice,
            [string[]]$FeatureTypes = @('PhoneSystem', 'CallingPlan'),
            [string]$AccountType = 'User'
        )

        return [PSCustomObject]@{
            UserPrincipalName        = $Upn
            Identity                 = "id-$Upn"
            AccountType              = $AccountType
            EnterpriseVoiceEnabled   = $EnterpriseVoice
            LineUri                  = $LineUri
            FeatureTypes             = $FeatureTypes
            OnlineVoiceRoutingPolicy = [PSCustomObject]@{ Name = 'US-Unrestricted' }
        }
    }

    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)

        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 32)
    }

    function Get-DefaultToolInput {
        return @{
            sourceUserUpn = 'alice@contoso.com'
            targetUserUpn = 'bob@contoso.com'
        }
    }

    function Invoke-SnapshotStage {
        param([hashtable]$ToolInput = $null)

        if ($null -eq $ToolInput) { $ToolInput = Get-DefaultToolInput }
        $json = New-StageInput -ToolInput $ToolInput
        return (& $script:RunScript -Stage snapshot -InputJson $json | ConvertFrom-Json -Depth 32)
    }
}

Describe 'move-number-between-users' {
    BeforeEach {
        $global:MnbuSourceUpn = 'alice@contoso.com'
        $global:MnbuTargetUpn = 'bob@contoso.com'
        $global:MnbuNumber = '+15551234567'
        $global:MnbuSourceLine = 'tel:+15551234567'
        $global:MnbuTargetLine = $null
        $global:MnbuSourceEv = $true
        $global:MnbuTargetEv = $true
        $global:MnbuTargetFeatures = @('PhoneSystem', 'CallingPlan')
        $global:MnbuTargetAccountType = 'User'
        $global:MnbuNumberType = 'CallingPlan'

        Mock Get-CsOnlineUser {
            if ($Identity -eq $global:MnbuSourceUpn) {
                return (New-TestUser -Upn $global:MnbuSourceUpn -LineUri $global:MnbuSourceLine -EnterpriseVoice $global:MnbuSourceEv)
            }

            return (New-TestUser -Upn $global:MnbuTargetUpn -LineUri $global:MnbuTargetLine -EnterpriseVoice $global:MnbuTargetEv -FeatureTypes $global:MnbuTargetFeatures -AccountType $global:MnbuTargetAccountType)
        }

        Mock Get-CsPhoneNumberAssignment {
            if ([string]::IsNullOrWhiteSpace($global:MnbuNumberType)) { return $null }

            return [PSCustomObject]@{
                TelephoneNumber      = $global:MnbuNumber
                NumberType           = $global:MnbuNumberType
                PstnAssignmentStatus = 'UserAssigned'
                AssignedPstnTargetId = "id-$($global:MnbuSourceUpn)"
            }
        }

        Mock Remove-CsPhoneNumberAssignment {
            if ($Identity -eq $global:MnbuTargetUpn) { $global:MnbuTargetLine = $null }
            else { $global:MnbuSourceLine = $null }
        }
        Mock Set-CsPhoneNumberAssignment {
            if (-not [string]::IsNullOrWhiteSpace($PhoneNumber)) {
                if ($Identity -eq $global:MnbuTargetUpn) { $global:MnbuTargetLine = "tel:$PhoneNumber" }
                else { $global:MnbuSourceLine = "tel:$PhoneNumber" }
            }
            else {
                if ($Identity -eq $global:MnbuTargetUpn) { $global:MnbuTargetEv = $EnterpriseVoiceEnabled }
                else { $global:MnbuSourceEv = $EnterpriseVoiceEnabled }
            }
        }
    }

    Context 'snapshot stage' {
        It 'captures both users, the number and its tenant type' {
            $snapshot = Invoke-SnapshotStage

            $snapshot.phoneNumber | Should -Be '+15551234567'
            $snapshot.phoneNumberType | Should -Be 'CallingPlan'
            $snapshot.source.userPrincipalName | Should -Be 'alice@contoso.com'
            $snapshot.source.lineUri | Should -Be '+15551234567'
            $snapshot.target.userPrincipalName | Should -Be 'bob@contoso.com'
            $snapshot.target.lineUri | Should -BeNullOrEmpty
            $snapshot.capturedAt | Should -Not -BeNullOrEmpty
        }

        It 'emits exactly one JSON result' {
            $result = & $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput))

            $result | Should -HaveCount 1
        }

        It 'honours an explicitly supplied phone number' {
            $toolInput = Get-DefaultToolInput
            $toolInput['phoneNumber'] = '+1 (555) 123-4567'

            $snapshot = Invoke-SnapshotStage -ToolInput $toolInput

            $snapshot.phoneNumber | Should -Be '+15551234567'
        }

        It 'rejects a phone number that is not E.164' {
            $toolInput = Get-DefaultToolInput
            $toolInput['phoneNumber'] = 'not-a-number'

            { & $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput $toolInput) } | Should -Throw '*E.164*'
        }

        It 'records a null number type when the number is outside tenant inventory' {
            $global:MnbuNumberType = $null

            $snapshot = Invoke-SnapshotStage

            $snapshot.phoneNumberType | Should -BeNullOrEmpty
        }

        It 'makes no write calls' {
            $null = Invoke-SnapshotStage

            Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }
    }

    Context 'preflight stage' {
        It 'passes every check for a well-formed move' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.checks | Should -HaveCount 7
            @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
            $parsed.summary | Should -Match 'All preflight checks passed'
        }

        It 'fails when the target is a resource account' {
            # A resource account is rejected by Teams with an opaque BadRequest, so
            # preflight has to catch it before anything is written.
            $global:MnbuTargetAccountType = 'ResourceAccount'
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            $failed = @($parsed.checks | Where-Object { $_.check -eq 'target is a user account' -and -not $_.passed })
            $failed | Should -HaveCount 1
            $failed[0].detail | Should -Match 'ResourceAccount'
        }

        It 'fails when the target has Phone System but no Calling Plan licence' {
            $global:MnbuTargetFeatures = @('PhoneSystem')
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'target user is licensed for the number type' -and -not $_.passed }) | Should -HaveCount 1
            @($parsed.checks | Where-Object { $_.check -eq 'target user is enterprise-voice capable' -and -not $_.passed }) | Should -HaveCount 0
        }

        It 'does not require a Calling Plan licence for a Direct Routing number' {
            $global:MnbuTargetFeatures = @('PhoneSystem')
            $global:MnbuNumberType = 'DirectRouting'
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
        }

        It 'fails when the target already holds a number' {
            $global:MnbuTargetLine = 'tel:+15559999999'
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            $failed = @($parsed.checks | Where-Object { -not $_.passed })
            $failed | Should -HaveCount 1
            $failed[0].check | Should -Be 'target user has no phone number assigned'
        }

        It 'fails when the source does not hold the number' {
            $global:MnbuSourceLine = $null
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'source user has the phone number assigned' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'fails when the target has no Phone System license reported' {
            $global:MnbuTargetEv = $false
            $global:MnbuTargetFeatures = @('Teams')
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'target user is enterprise-voice capable' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'does not fail the capability check when the tenant reports no license data' {
            $global:MnbuTargetEv = $false
            $global:MnbuTargetFeatures = @()
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'target user is enterprise-voice capable' -and -not $_.passed }) | Should -HaveCount 0
        }

        It 'fails when source and target are the same user' {
            $toolInput = Get-DefaultToolInput
            $toolInput['targetUserUpn'] = 'alice@contoso.com'
            $global:MnbuTargetUpn = 'alice@contoso.com'
            $snapshot = Invoke-SnapshotStage -ToolInput $toolInput
            $json = New-StageInput -ToolInput $toolInput -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage preflight -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'source and target are different users' -and -not $_.passed }) | Should -HaveCount 1
        }

        It 'requires the captured snapshot' {
            { & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-DefaultToolInput)) } | Should -Throw '*snapshot*'
        }
    }

    Context 'dryrun stage' {
        It 'renders the planned cmdlet calls without writing' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage dryrun -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.summary | Should -Match 'Would move \+15551234567'
            $parsed.after.plannedCommands | Should -HaveCount 2
            $parsed.after.plannedCommands[0] | Should -Match "Remove-CsPhoneNumberAssignment -Identity 'alice@contoso.com'"
            $parsed.after.plannedCommands[1] | Should -Match "Set-CsPhoneNumberAssignment -Identity 'bob@contoso.com'"
            $parsed.after.target.lineUri | Should -Be '+15551234567'
            $parsed.after.source.lineUri | Should -BeNullOrEmpty

            Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }

        It 'plans the enterprise voice enablement when the target is not enabled' {
            $global:MnbuTargetEv = $false
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage dryrun -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.after.plannedCommands | Should -HaveCount 3
            $parsed.after.plannedCommands[2] | Should -Match 'EnterpriseVoiceEnabled'
        }
    }

    Context 'execute stage' {
        It 'releases the number from the source and assigns it to the target' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage execute -InputJson $json | ConvertFrom-Json -Depth 32

            Should -Invoke Remove-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
                $Identity -eq 'alice@contoso.com' -and $PhoneNumber -eq '+15551234567' -and $PhoneNumberType -eq 'CallingPlan'
            }
            Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
                $Identity -eq 'bob@contoso.com' -and $PhoneNumber -eq '+15551234567'
            }

            $parsed.summary | Should -Match 'Moved \+15551234567 from alice@contoso.com to bob@contoso.com'
            $parsed.after.changed | Should -BeTrue
            $parsed.after.target.lineUri | Should -Be '+15551234567'
            $parsed.after.source.lineUri | Should -BeNullOrEmpty
        }

        It 'enables enterprise voice on the target when it is not already enabled' {
            $global:MnbuTargetEv = $false
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $null = & $script:RunScript -Stage execute -InputJson $json

            Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
                $Identity -eq 'bob@contoso.com' -and $EnterpriseVoiceEnabled -eq $true
            }
        }

        It 'is idempotent when the number is already assigned to the target' {
            $snapshot = Invoke-SnapshotStage
            $global:MnbuSourceLine = $null
            $global:MnbuTargetLine = 'tel:+15551234567'
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage execute -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.after.changed | Should -BeFalse
            $parsed.summary | Should -Match 'already assigned'
            Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }

        It 'refuses to write when the tenant drifted from the snapshot' {
            $snapshot = Invoke-SnapshotStage
            $global:MnbuSourceLine = 'tel:+15558888888'
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            { & $script:RunScript -Stage execute -InputJson $json } | Should -Throw '*no longer holds the number*'

            Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }

        It 'refuses to write when the number type is unknown' {
            $global:MnbuNumberType = $null
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            { & $script:RunScript -Stage execute -InputJson $json } | Should -Throw '*number type*'
        }
    }

    Context 'verify stage' {
        It 'passes every check once the move landed' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot
            $null = & $script:RunScript -Stage execute -InputJson $json

            $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.checks | Should -HaveCount 3
            @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
            $parsed.summary | Should -Match 'Verified'
        }

        It 'reports failing checks when the assignment did not land' {
            $snapshot = Invoke-SnapshotStage
            Mock Wait-ForCondition { return $false }
            $global:MnbuSourceLine = $null
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'number assigned to target' -and -not $_.passed }) | Should -HaveCount 1
            $parsed.summary | Should -Match 'verification check'
        }

        It 'reports the source still holding the number as a failure' {
            $snapshot = Invoke-SnapshotStage
            $global:MnbuTargetLine = 'tel:+15551234567'
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 32

            @($parsed.checks | Where-Object { $_.check -eq 'number released from source' -and -not $_.passed }) | Should -HaveCount 1
        }
    }

    Context 'rollback stage' {
        It 'returns the number to the source user' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot
            $null = & $script:RunScript -Stage execute -InputJson $json

            $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 32

            Should -Invoke Remove-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
                $Identity -eq 'bob@contoso.com'
            }
            Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
                $Identity -eq 'alice@contoso.com' -and $PhoneNumber -eq '+15551234567'
            }

            $parsed.after.restored | Should -BeTrue
            $parsed.after.source.lineUri | Should -Be '+15551234567'
            $parsed.after.target.lineUri | Should -BeNullOrEmpty
        }

        It 'is a no-op when the change never landed' {
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.after.restored | Should -BeTrue
            Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }

        It 'does nothing when the snapshot has no restorable assignment' {
            $global:MnbuSourceLine = $null
            $snapshot = Invoke-SnapshotStage
            $json = New-StageInput -ToolInput (Get-DefaultToolInput) -Snapshot $snapshot

            $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 32

            $parsed.after.restored | Should -BeFalse
            Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
            Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
        }
    }
}
