BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsCallingLineIdentity { param([string]$ErrorAction) }
    function Grant-CsCallingLineIdentity { param([string]$Identity, [AllowNull()][string]$PolicyName, [string]$ErrorAction) }
    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 20)
    }
    function Get-ToolInput { return @{ userUpn = 'alice@contoso.com'; policyName = 'Mask-Service' } }
    function Invoke-Snapshot {
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput (Get-ToolInput)) | ConvertFrom-Json -Depth 20)
    }
}

Describe 'set-caller-id-assignment' {
    BeforeEach {
        $global:CidAccountType = 'User'
        $global:CidPolicy = 'Old-Policy'
        $global:CidPolicyExists = $true

        Mock Get-CsOnlineUser {
            [PSCustomObject]@{
                UserPrincipalName = 'alice@contoso.com'
                AccountType = $global:CidAccountType
                CallingLineIdentity = if ($null -eq $global:CidPolicy) { $null } else { [PSCustomObject]@{ Name = $global:CidPolicy } }
            }
        }
        Mock Get-CsCallingLineIdentity {
            if ($global:CidPolicyExists) { @([PSCustomObject]@{ Identity = 'Tag:Mask-Service' }) } else { @() }
        }
        Mock Grant-CsCallingLineIdentity { $global:CidPolicy = $PolicyName }
    }

    It 'captures the original assignment and requested policy availability' {
        $snapshot = Invoke-Snapshot
        $snapshot.user.policyName | Should -Be 'Old-Policy'
        $snapshot.policyExists | Should -BeTrue
    }

    It 'passes two mirrored preflight checks' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 20
        $parsed.checks | Should -HaveCount 2
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rejects a missing caller ID policy' {
        $global:CidPolicyExists = $false
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 20
        @($parsed.checks | Where-Object { $_.check -eq 'requested caller ID policy exists' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'renders a dry run without granting the policy' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 20
        $parsed.after.plannedCommands[0] | Should -Match 'Grant-CsCallingLineIdentity'
        Should -Invoke Grant-CsCallingLineIdentity -Times 0 -Exactly
    }

    It 'grants the requested caller ID policy' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 20
        Should -Invoke Grant-CsCallingLineIdentity -Times 1 -Exactly -ParameterFilter { $PolicyName -eq 'Mask-Service' }
        $parsed.after.changed | Should -BeTrue
        $parsed.after.user.policyName | Should -Be 'Mask-Service'
    }

    It 'is idempotent when the policy is already assigned' {
        $global:CidPolicy = 'Mask-Service'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 20
        $parsed.after.changed | Should -BeFalse
        Should -Invoke Grant-CsCallingLineIdentity -Times 0 -Exactly
    }

    It 'refuses assignment drift after snapshot' {
        $snapshot = Invoke-Snapshot
        $global:CidPolicy = 'Unexpected-Policy'
        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*changed since the snapshot*'
        Should -Invoke Grant-CsCallingLineIdentity -Times 0 -Exactly
    }

    It 'verifies the requested assignment' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 20
        $parsed.checks[0].passed | Should -BeTrue
    }

    It 'rolls back to the original policy assignment' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json
        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 20
        $parsed.after.policyName | Should -Be 'Old-Policy'
        Should -Invoke Grant-CsCallingLineIdentity -Times 2 -Exactly
    }
}