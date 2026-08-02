BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsPhoneNumberAssignment { param([string]$TelephoneNumber, [string]$ErrorAction) }
    function Remove-CsPhoneNumberAssignment { param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [string]$ErrorAction) }
    function Set-CsPhoneNumberAssignment { param([string]$Identity, [string]$PhoneNumber, [string]$PhoneNumberType, [string]$ErrorAction) }

    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 24)
    }

    function Get-ToolInput { return @{ userUpn = 'alice@contoso.com'; phoneNumber = '+15551234567' } }

    function Invoke-Snapshot {
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput (Get-ToolInput)) | ConvertFrom-Json -Depth 24)
    }
}

Describe 'remove-phone-number' {
    BeforeEach {
        $global:RpnUpn = 'alice@contoso.com'
        $global:RpnObjectId = 'alice-id'
        $global:RpnAccountType = 'User'
        $global:RpnEnterpriseVoice = $true
        $global:RpnLineUri = 'tel:+15551234567'
        $global:RpnNumberType = 'CallingPlan'
        $global:RpnStatus = 'UserAssigned'
        $global:RpnTarget = 'alice-id'
        $global:RpnLocationId = '11111111-1111-1111-1111-111111111111'

        Mock Get-CsOnlineUser {
            [PSCustomObject]@{
                UserPrincipalName = $global:RpnUpn
                Identity = $global:RpnObjectId
                AccountType = $global:RpnAccountType
                EnterpriseVoiceEnabled = $global:RpnEnterpriseVoice
                LineUri = $global:RpnLineUri
            }
        }
        Mock Get-CsPhoneNumberAssignment {
            [PSCustomObject]@{
                TelephoneNumber = '+15551234567'
                NumberType = $global:RpnNumberType
                PstnAssignmentStatus = $global:RpnStatus
                AssignedPstnTargetId = $global:RpnTarget
                LocationId = $global:RpnLocationId
            }
        }
        Mock Remove-CsPhoneNumberAssignment {
            $global:RpnLineUri = $null
            $global:RpnStatus = 'Unassigned'
            $global:RpnTarget = $null
        }
        Mock Set-CsPhoneNumberAssignment {
            $global:RpnLineUri = "tel:$PhoneNumber"
            $global:RpnStatus = 'UserAssigned'
            $global:RpnTarget = $global:RpnObjectId
        }
    }

    It 'captures the current user and inventory assignment' {
        $snapshot = Invoke-Snapshot

        $snapshot.phoneNumber | Should -Be '+15551234567'
        $snapshot.phoneNumberType | Should -Be 'CallingPlan'
        $snapshot.user.enterpriseVoiceEnabled | Should -BeTrue
        $snapshot.assignedPstnTarget | Should -Be 'alice-id'
        $snapshot.locationId | Should -Be '11111111-1111-1111-1111-111111111111'
    }

    It 'resolves the phone number from the user when omitted' {
        $toolInput = Get-ToolInput
        $toolInput.Remove('phoneNumber')

        $snapshot = & $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput $toolInput) | ConvertFrom-Json -Depth 24

        $snapshot.phoneNumber | Should -Be '+15551234567'
    }

    It 'passes four mirrored preflight checks for a valid assignment' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.checks | Should -HaveCount 4
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rejects a resource account' {
        $global:RpnAccountType = 'ResourceAccount'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'target is a user account' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'rejects tenant inventory assigned to another target' {
        $global:RpnTarget = 'different-user'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'tenant inventory assigns the number to the target user' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'renders a dry run without removing the number or disabling enterprise voice' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.plannedCommands | Should -HaveCount 1
        $parsed.after.plannedCommands[0] | Should -Match 'Remove-CsPhoneNumberAssignment'
        $parsed.after.enterpriseVoiceEnabled | Should -BeTrue
        Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'removes the assignment while preserving enterprise voice' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        Should -Invoke Remove-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
            $PhoneNumber -eq '+15551234567' -and $PhoneNumberType -eq 'CallingPlan'
        }
        $parsed.after.changed | Should -BeTrue
        $parsed.after.user.lineUri | Should -BeNullOrEmpty
        $parsed.after.user.enterpriseVoiceEnabled | Should -BeTrue
    }

    It 'is idempotent when the number is already absent' {
        $snapshot = Invoke-Snapshot
        $global:RpnLineUri = $null
        $global:RpnStatus = 'Unassigned'
        $global:RpnTarget = $null

        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.changed | Should -BeFalse
        Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'refuses a different live number after the snapshot' {
        $snapshot = Invoke-Snapshot
        $global:RpnLineUri = 'tel:+15559999999'

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*tenant state changed*'
        Should -Invoke Remove-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'verifies release from both the user and tenant inventory' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 24

        $parsed.checks | Should -HaveCount 2
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rolls back by restoring the original number and type' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 24

        Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
            $PhoneNumber -eq '+15551234567' -and $PhoneNumberType -eq 'CallingPlan'
        }
        $parsed.after.restored | Should -BeTrue
        $parsed.after.user.lineUri | Should -Be '+15551234567'
    }

    It 'refuses rollback when another target claimed the released number' {
        $snapshot = Invoke-Snapshot
        $global:RpnLineUri = $null
        $global:RpnStatus = 'UserAssigned'
        $global:RpnTarget = 'different-user'

        { & $script:RunScript -Stage rollback -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*manual intervention*'
        Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
    }
}