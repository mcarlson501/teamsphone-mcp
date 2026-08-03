BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'
    function Get-CsPhoneNumberAssignment { param([string]$TelephoneNumber, [string]$ErrorAction) }
    function Get-CsOnlineApplicationInstance { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsOnlineApplicationInstanceAssociation { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsAutoAttendant { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsCallQueue { param([string]$Identity, [string]$ErrorAction) }
    function Get-CsOnlineUser { param([string]$Identity, [string]$ErrorAction) }
    function New-Input { @{ input = @{ dialedNumber = '+15550000001' }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5 }
}

Describe 'trace-call-flow' {
    BeforeEach {
        Mock Get-CsPhoneNumberAssignment { [PSCustomObject]@{ TelephoneNumber = $TelephoneNumber; AssignedPstnTargetId = 'ra-aa' } }
        Mock Get-CsOnlineApplicationInstance {
            [PSCustomObject]@{ ObjectId = $Identity; DisplayName = "Resource $Identity" }
        }
        Mock Get-CsOnlineApplicationInstanceAssociation {
            if ($Identity -eq 'ra-aa') { return [PSCustomObject]@{ ConfigurationId = 'aa-1'; ConfigurationType = 'AutoAttendant' } }
            return [PSCustomObject]@{ ConfigurationId = 'cq-1'; ConfigurationType = 'CallQueue' }
        }
        Mock Get-CsAutoAttendant {
            [PSCustomObject]@{
                Identity = 'aa-1'; Name = 'Main'
                DefaultCallFlow = [PSCustomObject]@{
                    Menu = [PSCustomObject]@{ MenuOptions = @(
                        [PSCustomObject]@{ DtmfResponse = 'Tone1'; Action = 'TransferCallToTarget'; CallTarget = [PSCustomObject]@{ Id = 'ra-cq'; Type = 'ApplicationEndpoint' } }
                    ) }
                }
                CallFlows = @()
            }
        }
        Mock Get-CsCallQueue {
            [PSCustomObject]@{ Identity = 'cq-1'; Name = 'Support'; Agents = @([PSCustomObject]@{ ObjectId = 'agent-1' }) }
        }
        Mock Get-CsOnlineUser { [PSCustomObject]@{ UserPrincipalName = 'user@contoso.com' } }
    }

    It 'traces a number through an auto attendant into a queue and its agents' {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.status | Should -Be 'resolved'
        $parsed.after.nodes.type | Should -Contain 'phoneNumber'
        $parsed.after.nodes.type | Should -Contain 'autoAttendant'
        $parsed.after.nodes.type | Should -Contain 'callQueue'
        $parsed.after.nodes.type | Should -Contain 'agent'
        $parsed.after.findings | Should -HaveCount 0
    }

    It 'flags a recursive application endpoint as a routing loop' {
        Mock Get-CsAutoAttendant {
            [PSCustomObject]@{
                Identity = 'aa-1'; Name = 'Looping'
                DefaultCallFlow = [PSCustomObject]@{
                    Menu = [PSCustomObject]@{ MenuOptions = @(
                        [PSCustomObject]@{ DtmfResponse = 'Tone1'; Action = 'TransferCallToTarget'; CallTarget = [PSCustomObject]@{ Id = 'ra-aa'; Type = 'ApplicationEndpoint' } }
                    ) }
                }
                CallFlows = @()
            }
        }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.findings.code | Should -Contain 'callFlowLoopDetected'
    }

    It 'flags a deleted application endpoint without aborting the graph' {
        Mock Get-CsOnlineApplicationInstance {
            if ($Identity -eq 'ra-cq') { throw 'Resource account not found.' }
            [PSCustomObject]@{ ObjectId = $Identity; DisplayName = "Resource $Identity" }
        }
        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json
        $parsed.after.findings.code | Should -Contain 'resourceAccountNotFound'
        ($parsed.after.nodes | Where-Object { $_.id -eq 'resourceAccount:ra-cq' }).status | Should -Be 'unresolved'
    }

    It 'deduplicates equivalent external phone target formats' {
        Mock Get-CsAutoAttendant {
            [PSCustomObject]@{
                Identity = 'aa-1'; Name = 'Main'
                DefaultCallFlow = [PSCustomObject]@{
                    Menu = [PSCustomObject]@{ MenuOptions = @(
                        [PSCustomObject]@{ DtmfResponse = 'Tone1'; Action = 'TransferCallToTarget'; CallTarget = [PSCustomObject]@{ Id = 'tel:+15550000005'; Type = 'Phone' } },
                        [PSCustomObject]@{ DtmfResponse = 'Tone1'; Action = 'TransferCallToTarget'; CallTarget = [PSCustomObject]@{ Id = '+1 (555) 000-0005'; Type = 'Phone' } }
                    ) }
                }
                CallFlows = @()
            }
        }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-Input) | ConvertFrom-Json

        $externalNumbers = @($parsed.after.nodes | Where-Object { $_.type -eq 'externalNumber' })
        $externalNumbers | Should -HaveCount 1
        $externalNumbers[0].id | Should -Be 'externalNumber:+15550000005'
        @($parsed.after.edges | Where-Object { $_.to -eq 'externalNumber:+15550000005' }) | Should -HaveCount 1
    }
}