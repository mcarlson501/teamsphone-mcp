BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsOnlineVoicemailUserSettings { param([string]$Identity, [string]$ErrorAction) }
    function Set-CsOnlineVoicemailUserSettings {
        param(
            [string]$Identity,
            [Nullable[bool]]$IsEnabled,
            [Nullable[bool]]$OofGreetingFollowAutomaticRepliesEnabled,
            [Nullable[bool]]$OofGreetingFollowCalendarEnabled,
            [string]$ErrorAction
        )
    }

    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 24)
    }
    function Get-ToolInput {
        return @{ userUpn = 'alice@contoso.com'; voicemailEnabled = $false; followAutomaticReplies = $true }
    }
    function Invoke-Snapshot {
        param([hashtable]$ToolInput = $null)
        if ($null -eq $ToolInput) { $ToolInput = Get-ToolInput }
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput $ToolInput) | ConvertFrom-Json -Depth 24)
    }
}

Describe 'update-user-voicemail-settings' {
    BeforeEach {
        $global:UvsAccountType = 'User'
        $global:UvsEnabled = $true
        $global:UvsAutomatic = $false
        $global:UvsCalendar = $false

        Mock Get-CsOnlineUser { [PSCustomObject]@{ UserPrincipalName = 'alice@contoso.com'; AccountType = $global:UvsAccountType } }
        Mock Get-CsOnlineVoicemailUserSettings {
            [PSCustomObject]@{
                IsEnabled = $global:UvsEnabled
                OofGreetingFollowAutomaticRepliesEnabled = $global:UvsAutomatic
                OofGreetingFollowCalendarEnabled = $global:UvsCalendar
            }
        }
        Mock Set-CsOnlineVoicemailUserSettings {
            if ($null -ne $IsEnabled) { $global:UvsEnabled = $IsEnabled }
            if ($null -ne $OofGreetingFollowAutomaticRepliesEnabled) { $global:UvsAutomatic = $OofGreetingFollowAutomaticRepliesEnabled }
            if ($null -ne $OofGreetingFollowCalendarEnabled) { $global:UvsCalendar = $OofGreetingFollowCalendarEnabled }
        }
    }

    It 'captures current and requested voicemail settings' {
        $snapshot = Invoke-Snapshot

        $snapshot.voicemailSettings.voicemailEnabled | Should -BeTrue
        $snapshot.requestedSettings.voicemailEnabled | Should -BeFalse
        $snapshot.requestedSettings.followAutomaticReplies | Should -BeTrue
    }

    It 'passes two mirrored preflight checks' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.checks | Should -HaveCount 2
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rejects an empty settings request' {
        $toolInput = @{ userUpn = 'alice@contoso.com' }
        $snapshot = Invoke-Snapshot -ToolInput $toolInput
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'at least one voicemail setting is requested' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'rejects a resource account' {
        $global:UvsAccountType = 'ResourceAccount'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'target is a user account' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'renders requested booleans without writing' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.voicemailEnabled | Should -BeFalse
        $parsed.after.followAutomaticReplies | Should -BeTrue
        $parsed.after.followCalendarEvents | Should -BeFalse
        Should -Invoke Set-CsOnlineVoicemailUserSettings -Times 0 -Exactly
    }

    It 'updates only requested settings' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        Should -Invoke Set-CsOnlineVoicemailUserSettings -Times 1 -Exactly -ParameterFilter {
            $IsEnabled -eq $false -and $OofGreetingFollowAutomaticRepliesEnabled -eq $true -and
            $null -eq $OofGreetingFollowCalendarEnabled
        }
        $parsed.after.changed | Should -BeTrue
        $parsed.after.voicemailSettings.followCalendarEvents | Should -BeFalse
    }

    It 'is idempotent when requested settings already match' {
        $global:UvsEnabled = $false
        $global:UvsAutomatic = $true
        $snapshot = Invoke-Snapshot

        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.changed | Should -BeFalse
        Should -Invoke Set-CsOnlineVoicemailUserSettings -Times 0 -Exactly
    }

    It 'refuses a conflicting setting change after snapshot' {
        $toolInput = @{ userUpn = 'alice@contoso.com'; followCalendarEvents = $true }
        $snapshot = Invoke-Snapshot -ToolInput $toolInput
        $global:UvsCalendar = $true
        $toolInput.followCalendarEvents = $false

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot) } | Should -Throw '*changed since the snapshot*'
        Should -Invoke Set-CsOnlineVoicemailUserSettings -Times 0 -Exactly
    }

    It 'verifies requested settings' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 24
        $parsed.checks[0].passed | Should -BeTrue
    }

    It 'rolls back requested settings to their original values' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 24

        $parsed.after.voicemailEnabled | Should -BeTrue
        $parsed.after.followAutomaticReplies | Should -BeFalse
        Should -Invoke Set-CsOnlineVoicemailUserSettings -Times 2 -Exactly
    }
}