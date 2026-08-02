BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

    function Get-CsCallQueue { param([string]$Identity, [string]$NameFilter, [string]$ErrorAction) }
    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function Set-CsCallQueue { param([string]$Identity, [string[]]$Users, [string]$ErrorAction) }

    function New-StageInput {
        param([hashtable]$ToolInput, [object]$Snapshot = $null)
        return (@{ input = $ToolInput; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 24)
    }
    function Get-ToolInput { return @{ callQueueIdentity = 'Sales'; agentUserUpns = @('new1@contoso.com', 'new2@contoso.com') } }
    function Invoke-Snapshot {
        param([hashtable]$ToolInput = $null)
        if ($null -eq $ToolInput) { $ToolInput = Get-ToolInput }
        return (& $script:RunScript -Stage snapshot -InputJson (New-StageInput -ToolInput $ToolInput) | ConvertFrom-Json -Depth 24)
    }
}

Describe 'update-callqueue-members' {
    BeforeEach {
        $global:UcmAgentIds = @('old-1', 'old-2')
        $global:UcmDistributionLists = @()
        $global:UcmMissingUpn = $null
        $global:UcmResourceUpn = $null

        Mock Get-CsCallQueue {
            [PSCustomObject]@{
                Identity = '11111111-1111-1111-1111-111111111111'
                Name = 'Sales'
                Agents = @($global:UcmAgentIds | ForEach-Object { [PSCustomObject]@{ ObjectId = $_ } })
                DistributionLists = $global:UcmDistributionLists
            }
        }
        Mock Get-CsOnlineUser {
            if ($Identity -eq $global:UcmMissingUpn) { throw 'User not found.' }
            [PSCustomObject]@{
                UserPrincipalName = $Identity
                Identity = "id-$Identity"
                AccountType = if ($Identity -eq $global:UcmResourceUpn) { 'ResourceAccount' } else { 'User' }
            }
        }
        Mock Set-CsCallQueue { $global:UcmAgentIds = @($Users) }
    }

    It 'captures queue membership and resolves requested agents' {
        $snapshot = Invoke-Snapshot

        $snapshot.queue.agentObjectIds | Should -HaveCount 2
        $snapshot.requestedAgents | Should -HaveCount 2
        $snapshot.requestedAgents[0].objectId | Should -Be 'id-new1@contoso.com'
    }

    It 'passes four mirrored preflight checks' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.checks | Should -HaveCount 4
        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 0
    }

    It 'rejects duplicate requested UPNs case-insensitively' {
        $toolInput = @{ callQueueIdentity = 'Sales'; agentUserUpns = @('agent@contoso.com', 'AGENT@contoso.com') }
        $snapshot = Invoke-Snapshot -ToolInput $toolInput
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'requested agents are unique' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'rejects unresolved and resource-account agents' {
        $global:UcmMissingUpn = 'new1@contoso.com'
        $global:UcmResourceUpn = 'new2@contoso.com'
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { -not $_.passed }) | Should -HaveCount 2
    }

    It 'rejects a queue with distribution-list agents' {
        $global:UcmDistributionLists = @('group-id')
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage preflight -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        @($parsed.checks | Where-Object { $_.check -eq 'queue uses direct user membership only' -and -not $_.passed }) | Should -HaveCount 1
    }

    It 'renders a dry run without updating the queue' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage dryrun -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.agentObjectIds | Should -HaveCount 2
        $parsed.after.plannedCommands[0] | Should -Match 'Set-CsCallQueue'
        Should -Invoke Set-CsCallQueue -Times 0 -Exactly
    }

    It 'replaces direct queue membership with resolved object IDs' {
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        Should -Invoke Set-CsCallQueue -Times 1 -Exactly -ParameterFilter {
            $Users.Count -eq 2 -and $Users -contains 'id-new1@contoso.com' -and $Users -contains 'id-new2@contoso.com'
        }
        $parsed.after.changed | Should -BeTrue
    }

    It 'supports clearing all direct agents' {
        $toolInput = @{ callQueueIdentity = 'Sales'; agentUserUpns = @() }
        $snapshot = Invoke-Snapshot -ToolInput $toolInput

        $null = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput $toolInput -Snapshot $snapshot)

        Should -Invoke Set-CsCallQueue -Times 1 -Exactly -ParameterFilter { $Users.Count -eq 0 }
    }

    It 'is idempotent when requested membership already exists' {
        $global:UcmAgentIds = @('id-new2@contoso.com', 'id-new1@contoso.com')
        $snapshot = Invoke-Snapshot
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) | ConvertFrom-Json -Depth 24

        $parsed.after.changed | Should -BeFalse
        Should -Invoke Set-CsCallQueue -Times 0 -Exactly
    }

    It 'refuses membership drift after the snapshot' {
        $snapshot = Invoke-Snapshot
        $global:UcmAgentIds = @('unexpected-user')

        { & $script:RunScript -Stage execute -InputJson (New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot) } | Should -Throw '*changed since the snapshot*'
        Should -Invoke Set-CsCallQueue -Times 0 -Exactly
    }

    It 'verifies the requested membership as a set' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage verify -InputJson $json | ConvertFrom-Json -Depth 24

        $parsed.checks[0].passed | Should -BeTrue
    }

    It 'rolls back to the original direct membership' {
        $snapshot = Invoke-Snapshot
        $json = New-StageInput -ToolInput (Get-ToolInput) -Snapshot $snapshot
        $null = & $script:RunScript -Stage execute -InputJson $json

        $parsed = & $script:RunScript -Stage rollback -InputJson $json | ConvertFrom-Json -Depth 24

        $parsed.after.agentObjectIds | Should -Be @('old-1', 'old-2')
        Should -Invoke Set-CsCallQueue -Times 2 -Exactly
    }
}