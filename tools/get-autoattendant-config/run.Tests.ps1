BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    # Stub so Pester can mock a cmdlet that ships with the MicrosoftTeams module,
    # which is not loaded during offline unit testing.
    function Get-CsAutoAttendant {
        param([string]$Identity, [string]$NameFilter, [string]$ErrorAction)
    }

    function New-StageInput {
        param([string]$AutoAttendantIdentity = 'Main Reception')

        return (@{ input = @{ autoAttendantIdentity = $AutoAttendantIdentity }; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5)
    }

    # Mock bodies execute in the invoked script's scope, so fixture data is
    # exposed through a function rather than a $script: variable.
    function New-SampleAttendant {
        [PSCustomObject]@{
            Identity             = '11111111-1111-1111-1111-111111111111'
            Name                 = 'Main Reception'
            LanguageId           = 'en-US'
            TimeZoneId           = 'Pacific Standard Time'
            VoiceId              = 'Female'
            VoiceResponseEnabled = $true
            Operator             = [PSCustomObject]@{ Id = '99999999-9999-9999-9999-999999999999'; Type = 'User' }
            DefaultCallFlow      = [PSCustomObject]@{
                Id        = 'flow-default'
                Name      = 'Business Hours Flow'
                Greetings = @(
                    [PSCustomObject]@{ ActiveType = 'TextToSpeech'; TextToSpeechPrompt = 'Welcome to Contoso.' }
                )
                Menu      = [PSCustomObject]@{
                    Name                  = 'Main Menu'
                    DirectorySearchMethod = 'ByName'
                    Prompts               = @(
                        [PSCustomObject]@{ ActiveType = 'TextToSpeech'; TextToSpeechPrompt = 'Press 1 for sales.' }
                    )
                    MenuOptions           = @(
                        [PSCustomObject]@{
                            DtmfResponse   = 'Tone1'
                            VoiceResponses = @('Sales')
                            Action         = 'TransferCallToTarget'
                            CallTarget     = [PSCustomObject]@{ Id = 'cq-1'; Type = 'ApplicationEndpoint' }
                        },
                        [PSCustomObject]@{
                            DtmfResponse   = 'Tone0'
                            VoiceResponses = @()
                            Action         = 'Operator'
                            CallTarget     = $null
                        }
                    )
                }
            }
            CallFlows                = @(
                [PSCustomObject]@{
                    Id        = 'flow-after-hours'
                    Name      = 'After Hours Flow'
                    Greetings = @()
                    Menu      = [PSCustomObject]@{
                        Name        = 'After Hours Menu'
                        Prompts     = @()
                        MenuOptions = @(
                            [PSCustomObject]@{ DtmfResponse = 'Automatic'; Action = 'DisconnectCall' }
                        )
                    }
                }
            )
            CallHandlingAssociations = @(
                [PSCustomObject]@{ Type = 'AfterHours'; ScheduleId = 'sched-1'; CallFlowId = 'flow-after-hours'; Enabled = $true },
                [PSCustomObject]@{ Type = 'Holiday'; ScheduleId = 'sched-2'; CallFlowId = 'flow-after-hours'; Enabled = $true }
            )
            ApplicationInstances     = @('ra-1')
        }
    }
}

Describe 'get-autoattendant-config' {
    Context 'execute stage' {
        It 'returns the attendant configuration with a flattened menu tree' {
            Mock Get-CsAutoAttendant { New-SampleAttendant }

            $result = & $script:RunScript -Stage execute -InputJson (New-StageInput)

            $result | Should -HaveCount 1
            $parsed = $result | ConvertFrom-Json
            $parsed.summary | Should -Match 'Main Reception'
            $parsed.summary | Should -Match '3 menu options'
            $parsed.after.timeZoneId | Should -Be 'Pacific Standard Time'
            $parsed.after.operator.type | Should -Be 'User'
            $parsed.after.defaultCallFlow.greetings[0].textToSpeech | Should -Be 'Welcome to Contoso.'
            $parsed.after.defaultCallFlow.menuOptions | Should -HaveCount 2
            $parsed.after.defaultCallFlow.menuOptions[0].callTarget.id | Should -Be 'cq-1'
            $parsed.after.defaultCallFlow.menuOptions[1].callTarget | Should -BeNullOrEmpty
            $parsed.after.callFlowCount | Should -Be 1
            $parsed.after.scheduleIds | Should -HaveCount 2
            $parsed.after.resourceAccountIds | Should -HaveCount 1
        }

        It 'looks the attendant up by identity when a GUID is supplied' {
            Mock Get-CsAutoAttendant { New-SampleAttendant }

            $null = & $script:RunScript -Stage execute -InputJson (New-StageInput -AutoAttendantIdentity '11111111-1111-1111-1111-111111111111')

            Should -Invoke Get-CsAutoAttendant -Times 1 -ParameterFilter { $null -ne $Identity }
        }

        It 'throws when no attendant matches the supplied name' {
            Mock Get-CsAutoAttendant { @() }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput -AutoAttendantIdentity 'Missing') } | Should -Throw '*was not found*'
        }

        It 'throws when the supplied name is ambiguous' {
            Mock Get-CsAutoAttendant {
                @(
                    [PSCustomObject]@{ Identity = 'a'; Name = 'Main Reception' },
                    [PSCustomObject]@{ Identity = 'b'; Name = 'Main Reception' }
                )
            }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw '*ambiguous*'
        }

        It 'retries once when the tenant throttles the request' {
            $global:ThrottleAttempts = 0
            Mock Get-CsAutoAttendant {
                $global:ThrottleAttempts++
                if ($global:ThrottleAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
                New-SampleAttendant
            }

            $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

            $global:ThrottleAttempts | Should -Be 2
            $parsed.after.name | Should -Be 'Main Reception'
        }

        It 'surfaces a terminating error when the read fails' {
            Mock Get-CsAutoAttendant { throw 'Access denied.' }

            { & $script:RunScript -Stage execute -InputJson (New-StageInput) } | Should -Throw
        }
    }

    Context 'unsupported stages' {
        It 'throws for a stage the read tool does not implement' {
            { & $script:RunScript -Stage dryrun -InputJson (New-StageInput) } | Should -Throw
        }
    }
}
