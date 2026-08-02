BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsOnlineVoiceRoutingPolicy { param([string]$ErrorAction) }
    function Get-CsTenantDialPlan { param([string]$ErrorAction) }
    function Get-CsTeamsCallingPolicy { param([string]$ErrorAction) }
    function Grant-CsOnlineVoiceRoutingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTenantDialPlan { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function Grant-CsTeamsCallingPolicy { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }

    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 24)
    }

    function Get-ToolInput {
        return @{
            userUpn = 'bob@contoso.com'
            onlineVoiceRoutingPolicy = 'New-Voice'
            tenantDialPlan = 'New-Dial'
            teamsCallingPolicy = 'New-Calling'
        }
    }

    function Invoke-Snapshot {
        param([hashtable]$ToolInput = $null)
        if ($null -eq $ToolInput) { $ToolInput = Get-ToolInput }
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput $ToolInput) | ConvertFrom-Json -Depth 24)
    }
}

Describe 'update-user-calling-policies' {
    BeforeEach {
        $global:UcpAccountType = 'User'
        $global:UcpVoice = 'Old-Voice'
        $global:UcpDial = 'Old-Dial'
        $global:UcpCalling = 'Old-Calling'
        $global:UcpFailCalling = $false

        Mock Get-CsOnlineUser {
            [PSCustomObject]@{
                UserPrincipalName = 'bob@contoso.com'
                AccountType = $global:UcpAccountType
                OnlineVoiceRoutingPolicy = if ($null -eq $global:UcpVoice) { $null } else { [PSCustomObject]@{ Name = $global:UcpVoice } }
                TenantDialPlan = if ($null -eq $global:UcpDial) { $null } else { [PSCustomObject]@{ Name = $global:UcpDial } }
                TeamsCallingPolicy = if ($null -eq $global:UcpCalling) { $null } else { [PSCustomObject]@{ Name = $global:UcpCalling } }
            }
        }
        Mock Get-CsOnlineVoiceRoutingPolicy { @([PSCustomObject]@{ Identity = 'Tag:New-Voice' }) }
        Mock Get-CsTenantDialPlan { @([PSCustomObject]@{ Identity = 'Tag:New-Dial' }) }
        Mock Get-CsTeamsCallingPolicy { @([PSCustomObject]@{ Identity = 'Tag:New-Calling' }) }
        Mock Grant-CsOnlineVoiceRoutingPolicy { $global:UcpVoice = $PolicyName }
        Mock Grant-CsTenantDialPlan { $global:UcpDial = $PolicyName }
        Mock Grant-CsTeamsCallingPolicy {
            if ($global:UcpFailCalling) { throw 'Calling policy failure.' }
            $global:UcpCalling = $PolicyName
        }
    }

    It 'captures original assignments and policy availability' {
        $snapshot = Invoke-Snapshot

        $snapshot.user.onlineVoiceRoutingPolicy | Should -Be 'Old-Voice'
        $snapshot.requestedPolicies.teamsCallingPolicy | Should -Be 'New-Calling'
        $snapshot.policyAvailability.tenantDialPlan | Should -BeTrue
    }

    It 'passes three mirrored preflight checks' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.checks | Should -HaveCount 3
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rejects an empty update' {
        $toolInput = @{ userUpn = 'bob@contoso.com' }
        $snapshot = Invoke-Snapshot -ToolInput $toolInput
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'at least one policy assignment is requested' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'rejects a missing requested policy' {
        Mock Get-CsTenantDialPlan { @() }
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'requested policies exist' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'renders only changed assignments in dry run' {
        $global:UcpVoice = 'New-Voice'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.plannedCommands | Should -HaveCount 2
        $parsed.after.plannedCommands[0] | Should -Match 'Grant-CsTenantDialPlan'
        Should -Invoke Grant-CsTenantDialPlan -Times 0 -Exactly
    }

    It 'grants all requested policies' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        Should -Invoke Grant-CsOnlineVoiceRoutingPolicy -Times 1 -Exactly -ParameterFilter { $PolicyName -eq 'New-Voice' }
        Should -Invoke Grant-CsTenantDialPlan -Times 1 -Exactly -ParameterFilter { $PolicyName -eq 'New-Dial' }
        Should -Invoke Grant-CsTeamsCallingPolicy -Times 1 -Exactly -ParameterFilter { $PolicyName -eq 'New-Calling' }
        $parsed.after.changed | Should -BeTrue
        $parsed.after.changedPolicies | Should -HaveCount 3
    }

    It 'is idempotent when all requested policies are assigned' {
        $global:UcpVoice = 'New-Voice'
        $global:UcpDial = 'New-Dial'
        $global:UcpCalling = 'New-Calling'
        $snapshot = Invoke-Snapshot

        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.changed | Should -BeFalse
        Should -Invoke Grant-CsOnlineVoiceRoutingPolicy -Times 0 -Exactly
    }

    It 'revalidates policy existence before writing' {
        $snapshot = Invoke-Snapshot
        Mock Get-CsTeamsCallingPolicy { @() }

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*no longer exists*'
        Should -Invoke Grant-CsOnlineVoiceRoutingPolicy -Times 0 -Exactly
    }

    It 'restores earlier assignments when a later grant fails' {
        $snapshot = Invoke-Snapshot
        $global:UcpFailCalling = $true

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*Calling policy failure*'
        $global:UcpVoice | Should -Be 'Old-Voice'
        $global:UcpDial | Should -Be 'Old-Dial'
        $global:UcpCalling | Should -Be 'Old-Calling'
    }

    It 'verifies requested assignments' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 24

        $parsed.checks | Should -HaveCount 1
        $parsed.checks[0].passed | Should -BeTrue
    }

    It 'rolls back requested families to original assignments' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 24

        $parsed.after.onlineVoiceRoutingPolicy | Should -Be 'Old-Voice'
        $parsed.after.tenantDialPlan | Should -Be 'Old-Dial'
        $parsed.after.teamsCallingPolicy | Should -Be 'Old-Calling'
    }
}