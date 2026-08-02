BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsPhoneNumberAssignment { param([string]$TelephoneNumber, [string]$ErrorAction) }
    function Get-CsOnlineLisLocation { param([Guid]$LocationId, [string]$ErrorAction) }
    function Set-CsPhoneNumberAssignment { param([string]$PhoneNumber, [string]$LocationId, [string]$ErrorAction) }
    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 24)
    }
    function Get-ToolInput { return @{ userUpn = 'alice@contoso.com'; locationId = '22222222-2222-2222-2222-222222222222' } }
    function Invoke-Snapshot {
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput (Get-ToolInput)) | ConvertFrom-Json -Depth 24)
    }
}

Describe 'update-user-emergency-location' {
    BeforeEach {
        $global:UelLineUri = 'tel:+15551234567'
        $global:UelAccountType = 'User'
        $global:UelNumberType = 'CallingPlan'
        $global:UelLocationId = '11111111-1111-1111-1111-111111111111'
        $global:UelAssignedTarget = 'alice-id'
        $global:UelLocationExists = $true
        $global:UelLocationValidated = $true

        Mock Get-CsOnlineUser {
            [PSCustomObject]@{ UserPrincipalName = 'alice@contoso.com'; Identity = 'alice-id'; AccountType = $global:UelAccountType; LineUri = $global:UelLineUri }
        }
        Mock Get-CsPhoneNumberAssignment {
            [PSCustomObject]@{
                TelephoneNumber = '+15551234567'
                NumberType = $global:UelNumberType
                LocationId = $global:UelLocationId
                PstnAssignmentStatus = 'UserAssigned'
                AssignedPstnTargetId = $global:UelAssignedTarget
            }
        }
        Mock Get-CsOnlineLisLocation {
            if (-not $global:UelLocationExists) { return @() }
            [PSCustomObject]@{
                LocationId = $LocationId
                CivicAddressId = 'civic-id'
                Description = 'Seattle office'
                City = 'Seattle'
                CountryOrRegion = 'US'
                IsValidated = $global:UelLocationValidated
            }
        }
        Mock Set-CsPhoneNumberAssignment {
            $global:UelLocationId = if ($LocationId -eq 'null') { $null } else { $LocationId }
        }
    }

    It 'captures the user number, current location, and requested validated location' {
        $snapshot = Invoke-Snapshot
        $snapshot.number.phoneNumber | Should -Be '+15551234567'
        $snapshot.number.locationId | Should -Be '11111111-1111-1111-1111-111111111111'
        $snapshot.requestedLocation.validated | Should -BeTrue
    }

    It 'passes five mirrored preflight checks' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24
        $parsed.checks | Should -HaveCount 5
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rejects an unvalidated emergency location' {
        $global:UelLocationValidated = $false
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24
        @($parsed.checks | Where-Object { $_.check -eq 'requested emergency location is validated' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'rejects non-restorable missing original location for Calling Plan' {
        $global:UelLocationId = $null
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24
        @($parsed.checks | Where-Object { $_.check -eq 'original emergency location can be restored' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'allows a missing original location for Direct Routing rollback' {
        $global:UelLocationId = $null
        $global:UelNumberType = 'DirectRouting'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'renders the phone-number location update without writing' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24
        $parsed.after.plannedCommands[0] | Should -Match "-PhoneNumber '\+15551234567'.*-LocationId"
        Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'assigns the validated location to the user phone number' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24
        Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter {
            $PhoneNumber -eq '+15551234567' -and $LocationId -eq '22222222-2222-2222-2222-222222222222'
        }
        $parsed.after.changed | Should -BeTrue
        $parsed.after.number.locationId | Should -Be '22222222-2222-2222-2222-222222222222'
    }

    It 'refuses a non-restorable Calling Plan location change during execute' {
        $global:UelLocationId = $null
        $snapshot = Invoke-Snapshot

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*cannot be safely restored*'
        Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'is idempotent when the requested location is assigned' {
        $global:UelLocationId = '22222222-2222-2222-2222-222222222222'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24
        $parsed.after.changed | Should -BeFalse
        Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'refuses phone number drift after snapshot' {
        $snapshot = Invoke-Snapshot
        $global:UelLineUri = 'tel:+15559999999'
        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*phone number changed*'
        Should -Invoke Set-CsPhoneNumberAssignment -Times 0 -Exactly
    }

    It 'verifies the requested location assignment' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 24
        $parsed.checks[0].passed | Should -BeTrue
    }

    It 'rolls back to the original location' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 24
        $parsed.after.locationId | Should -Be '11111111-1111-1111-1111-111111111111'
        Should -Invoke Set-CsPhoneNumberAssignment -Times 2 -Exactly
    }

    It 'rolls back a new Direct Routing location using literal null' {
        $global:UelLocationId = $null
        $global:UelNumberType = 'DirectRouting'
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $null = & $script:RunScript -Stage rollback -InputJson $json
        Should -Invoke Set-CsPhoneNumberAssignment -Times 1 -Exactly -ParameterFilter { $LocationId -eq 'null' }
        $global:UelLocationId | Should -BeNullOrEmpty
    }
}