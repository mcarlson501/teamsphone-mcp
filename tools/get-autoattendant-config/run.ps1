param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("snapshot", "preflight", "dryrun", "execute", "verify", "rollback")]
    [string]$Stage,

    [Parameter(Mandatory = $true)]
    [string]$InputJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..' 'common' 'TeamsPhoneMcp.Common.psm1') -Force -DisableNameChecking

function ConvertTo-CallTarget {
    param($Target)

    if ($null -eq $Target) { return $null }

    return [ordered]@{
        id   = [string](Get-PropertyValue -InputObject $Target -Name 'Id')
        type = [string](Get-PropertyValue -InputObject $Target -Name 'Type')
    }
}

function ConvertTo-Prompt {
    param($Prompt)

    if ($null -eq $Prompt) { return $null }

    $textToSpeech = Get-PropertyValue -InputObject $Prompt -Name 'TextToSpeechPrompt'
    $audioFile = Get-PropertyValue -InputObject $Prompt -Name 'AudioFilePrompt'

    return [ordered]@{
        activeType     = [string](Get-PropertyValue -InputObject $Prompt -Name 'ActiveType')
        textToSpeech   = if ($null -ne $textToSpeech) { [string]$textToSpeech } else { $null }
        audioFileId    = if ($null -ne $audioFile) { [string](Get-PropertyValue -InputObject $audioFile -Name 'Id') } else { $null }
        audioFileName  = if ($null -ne $audioFile) { [string](Get-PropertyValue -InputObject $audioFile -Name 'FileName') } else { $null }
    }
}

function ConvertTo-MenuOption {
    param($Option)

    return [ordered]@{
        dtmfResponse   = [string](Get-PropertyValue -InputObject $Option -Name 'DtmfResponse')
        voiceResponses = @(Get-PropertyValue -InputObject $Option -Name 'VoiceResponses' -Default @() | ForEach-Object { [string]$_ })
        action         = [string](Get-PropertyValue -InputObject $Option -Name 'Action')
        callTarget     = ConvertTo-CallTarget -Target (Get-PropertyValue -InputObject $Option -Name 'CallTarget')
    }
}

function ConvertTo-CallFlow {
    param($CallFlow)

    if ($null -eq $CallFlow) { return $null }

    $menu = Get-PropertyValue -InputObject $CallFlow -Name 'Menu'
    # An `if` used as an expression collapses an empty array to $null, so menu
    # derived collections are built before the result literal.
    $menuPrompts = @()
    $menuOptions = @()
    $menuName = $null
    $directorySearch = $null
    if ($null -ne $menu) {
        $menuName = [string](Get-PropertyValue -InputObject $menu -Name 'Name')
        $directorySearch = [string](Get-PropertyValue -InputObject $menu -Name 'DirectorySearchMethod')
        $menuPrompts = @(Get-PropertyValue -InputObject $menu -Name 'Prompts' -Default @() | ForEach-Object { ConvertTo-Prompt -Prompt $_ })
        $menuOptions = @(Get-PropertyValue -InputObject $menu -Name 'MenuOptions' -Default @() | ForEach-Object { ConvertTo-MenuOption -Option $_ })
    }

    return [ordered]@{
        id              = [string](Get-PropertyValue -InputObject $CallFlow -Name 'Id')
        name            = [string](Get-PropertyValue -InputObject $CallFlow -Name 'Name')
        greetings       = @(Get-PropertyValue -InputObject $CallFlow -Name 'Greetings' -Default @() | ForEach-Object { ConvertTo-Prompt -Prompt $_ })
        menuName        = $menuName
        menuPrompts     = $menuPrompts
        directorySearch = $directorySearch
        menuOptions     = $menuOptions
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$autoAttendantIdentity = [string](Get-PropertyValue -InputObject $toolInput -Name 'autoAttendantIdentity')

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        if (Test-IsGuid -Value $autoAttendantIdentity) {
            $attendants = @(Invoke-WithRetry -ScriptBlock { Get-CsAutoAttendant -Identity $autoAttendantIdentity -ErrorAction Stop })
        }
        else {
            $attendants = @(Invoke-WithRetry -ScriptBlock { Get-CsAutoAttendant -NameFilter $autoAttendantIdentity -ErrorAction Stop })
            $attendants = @($attendants | Where-Object {
                    [string](Get-PropertyValue -InputObject $_ -Name 'Name') -eq $autoAttendantIdentity
                })
        }

        if ($attendants.Count -eq 0) {
            throw "Auto attendant '$autoAttendantIdentity' was not found."
        }

        if ($attendants.Count -gt 1) {
            throw "Auto attendant '$autoAttendantIdentity' is ambiguous; $($attendants.Count) attendants share that name. Retry with the attendant identity."
        }

        $attendant = $attendants[0]
        $defaultCallFlow = ConvertTo-CallFlow -CallFlow (Get-PropertyValue -InputObject $attendant -Name 'DefaultCallFlow')
        $callFlows = @(Get-PropertyValue -InputObject $attendant -Name 'CallFlows' -Default @() | ForEach-Object { ConvertTo-CallFlow -CallFlow $_ })

        $callHandlingAssociations = @()
        foreach ($association in @(Get-PropertyValue -InputObject $attendant -Name 'CallHandlingAssociations' -Default @())) {
            $callHandlingAssociations += [ordered]@{
                type       = [string](Get-PropertyValue -InputObject $association -Name 'Type')
                scheduleId = [string](Get-PropertyValue -InputObject $association -Name 'ScheduleId')
                callFlowId = [string](Get-PropertyValue -InputObject $association -Name 'CallFlowId')
                enabled    = Get-PropertyValue -InputObject $association -Name 'Enabled'
            }
        }

        $after = [ordered]@{
            identity                 = [string](Get-PropertyValue -InputObject $attendant -Name 'Identity')
            name                     = [string](Get-PropertyValue -InputObject $attendant -Name 'Name')
            languageId               = [string](Get-PropertyValue -InputObject $attendant -Name 'LanguageId')
            timeZoneId               = [string](Get-PropertyValue -InputObject $attendant -Name 'TimeZoneId')
            voiceId                  = [string](Get-PropertyValue -InputObject $attendant -Name 'VoiceId')
            enableVoiceResponse      = Get-PropertyValue -InputObject $attendant -Name 'VoiceResponseEnabled'
            operator                 = ConvertTo-CallTarget -Target (Get-PropertyValue -InputObject $attendant -Name 'Operator')
            defaultCallFlow          = $defaultCallFlow
            callFlowCount            = $callFlows.Count
            callFlows                = $callFlows
            callHandlingAssociations = $callHandlingAssociations
            scheduleIds              = @($callHandlingAssociations | ForEach-Object { $_.scheduleId } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
            resourceAccountIds       = @(Get-PropertyValue -InputObject $attendant -Name 'ApplicationInstances' -Default @() | ForEach-Object { [string]$_ })
        }

        $menuOptionCount = 0
        if ($null -ne $defaultCallFlow) { $menuOptionCount += @($defaultCallFlow.menuOptions).Count }
        foreach ($callFlow in $callFlows) { $menuOptionCount += @($callFlow.menuOptions).Count }

        $summary = "Retrieved auto attendant '$($after.name)' with $menuOptionCount menu options across $($callFlows.Count + 1) call flows."
        return (Write-StageResult -Summary $summary -After $after)
    }
    default {
        throw "Tool 'get-autoattendant-config' does not implement stage '$Stage'."
    }
}
