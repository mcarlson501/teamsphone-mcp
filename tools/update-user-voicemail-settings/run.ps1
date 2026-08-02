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

$script:SettingMap = [ordered]@{
    voicemailEnabled       = 'IsEnabled'
    followAutomaticReplies = 'OofGreetingFollowAutomaticRepliesEnabled'
    followCalendarEvents   = 'OofGreetingFollowCalendarEnabled'
}

function Test-HasProperty {
    param([AllowNull()][object]$InputObject, [Parameter(Mandatory = $true)][string]$Name)
    if ($InputObject -is [System.Collections.IDictionary]) {
        return $InputObject.Contains($Name)
    }
    return $null -ne $InputObject -and $InputObject.PSObject.Properties.Name -contains $Name
}

function Get-RequestedSettings {
    param([Parameter(Mandatory = $true)][object]$InputObject)

    $settings = [ordered]@{}
    foreach ($name in $script:SettingMap.Keys) {
        if (Test-HasProperty -InputObject $InputObject -Name $name) {
            $settings[$name] = [bool](Get-PropertyValue -InputObject $InputObject -Name $name)
        }
    }
    return $settings
}

function Get-UserState {
    param([Parameter(Mandatory = $true)][string]$Upn)

    $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    if ($null -eq $user) { throw "User '$Upn' was not found in the tenant." }
    return [ordered]@{
        userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        accountType       = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
    }
}

function Get-VoicemailState {
    param([Parameter(Mandatory = $true)][string]$Upn)

    $settings = Invoke-WithRetry -ScriptBlock { Get-CsOnlineVoicemailUserSettings -Identity $Upn -ErrorAction Stop }
    if ($null -eq $settings) { throw "Voicemail settings for '$Upn' were not found." }
    return [ordered]@{
        voicemailEnabled       = [bool](Get-PropertyValue -InputObject $settings -Name 'IsEnabled' -Default $false)
        followAutomaticReplies = [bool](Get-PropertyValue -InputObject $settings -Name 'OofGreetingFollowAutomaticRepliesEnabled' -Default $false)
        followCalendarEvents   = [bool](Get-PropertyValue -InputObject $settings -Name 'OofGreetingFollowCalendarEnabled' -Default $false)
    }
}

function Set-VoicemailState {
    param(
        [Parameter(Mandatory = $true)][string]$Upn,
        [Parameter(Mandatory = $true)][object]$Settings
    )

    $parameters = @{ Identity = $Upn; ErrorAction = 'Stop' }
    foreach ($name in $script:SettingMap.Keys) {
        if (Test-HasProperty -InputObject $Settings -Name $name) {
            $parameters[$script:SettingMap[$name]] = [bool](Get-PropertyValue -InputObject $Settings -Name $name)
        }
    }
    $null = Invoke-WithRetry -ScriptBlock { Set-CsOnlineVoicemailUserSettings @parameters }
}

function Test-SettingsMatch {
    param([Parameter(Mandatory = $true)][object]$Actual, [Parameter(Mandatory = $true)][object]$Expected)

    foreach ($name in $script:SettingMap.Keys) {
        if (-not (Test-HasProperty -InputObject $Expected -Name $name)) { continue }
        if ([bool](Get-PropertyValue -InputObject $Actual -Name $name) -ne [bool](Get-PropertyValue -InputObject $Expected -Name $name)) {
            return $false
        }
    }
    return $true
}

function New-Check {
    param([string]$Check, [bool]$Passed, [AllowNull()][string]$Detail)
    return [ordered]@{ check = $Check; passed = $Passed; detail = $Detail }
}

function Assert-Snapshot {
    param([AllowNull()][object]$Snapshot, [Parameter(Mandatory = $true)][string]$StageName)
    if ($null -eq $Snapshot) { throw "Stage '$StageName' requires the captured snapshot but none was supplied." }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$snapshot = Get-PropertyValue -InputObject $payload -Name 'snapshot'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')
$requestedSettings = Get-RequestedSettings -InputObject $toolInput

switch ($Stage) {
    'snapshot' {
        return (Write-StageSnapshot -State ([ordered]@{
            user              = Get-UserState -Upn $userUpn
            voicemailSettings = Get-VoicemailState -Upn $userUpn
            requestedSettings = $requestedSettings
            capturedAt        = (Get-Date).ToUniversalTime().ToString('o')
        }))
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $accountType = [string](Get-PropertyValue -InputObject $user -Name 'accountType')
        $isUser = [string]::IsNullOrWhiteSpace($accountType) -or [string]::Equals($accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)
        $hasSettings = $requestedSettings.Count -gt 0
        $checks = @(
            (New-Check -Check 'target is a user account' -Passed $isUser -Detail $(if ($isUser) { "$userUpn is a user account." } else { "$userUpn is a $accountType." })),
            (New-Check -Check 'at least one voicemail setting is requested' -Passed $hasSettings -Detail $(if ($hasSettings) { "$($requestedSettings.Count) voicemail setting(s) requested." } else { 'No voicemail settings were requested.' }))
        )
        $failed = @($checks | Where-Object { -not $_.passed }).Count
        return (Write-StageResult -Summary $(if ($failed -eq 0) { "All preflight checks passed for $userUpn voicemail settings." } else { "$failed preflight check(s) failed; voicemail settings were not updated." }) -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $current = Get-PropertyValue -InputObject $snapshot -Name 'voicemailSettings'
        $after = [ordered]@{}
        $parameters = @()
        foreach ($name in $script:SettingMap.Keys) {
            $after[$name] = if (Test-HasProperty -InputObject $requestedSettings -Name $name) {
                [bool](Get-PropertyValue -InputObject $requestedSettings -Name $name)
            } else {
                [bool](Get-PropertyValue -InputObject $current -Name $name)
            }
            if (Test-HasProperty -InputObject $requestedSettings -Name $name) {
                $parameters += "-$($script:SettingMap[$name]) `$$([bool](Get-PropertyValue -InputObject $requestedSettings -Name $name))"
            }
        }
        $after.plannedCommands = @("Set-CsOnlineVoicemailUserSettings -Identity '$userUpn' $($parameters -join ' ')")
        return (Write-StageResult -Summary "Would update $($requestedSettings.Count) voicemail setting(s) for $userUpn." -After $after)
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'voicemailSettings'
        $liveUser = Get-UserState -Upn $userUpn
        if (-not [string]::IsNullOrWhiteSpace($liveUser.accountType) -and -not [string]::Equals($liveUser.accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$userUpn is not a user account; nothing was changed."
        }
        $live = Get-VoicemailState -Upn $userUpn
        foreach ($name in $script:SettingMap.Keys) {
            if (-not (Test-HasProperty -InputObject $requestedSettings -Name $name)) { continue }
            $currentValue = [bool](Get-PropertyValue -InputObject $live -Name $name)
            $originalValue = [bool](Get-PropertyValue -InputObject $original -Name $name)
            $requestedValue = [bool](Get-PropertyValue -InputObject $requestedSettings -Name $name)
            if ($currentValue -ne $originalValue -and $currentValue -ne $requestedValue) {
                throw "Voicemail setting '$name' changed since the snapshot; nothing was changed."
            }
        }

        if (Test-SettingsMatch -Actual $live -Expected $requestedSettings) {
            return (Write-StageResult -Summary "$userUpn already has the requested voicemail settings." -After ([ordered]@{ voicemailSettings = $live; changed = $false }))
        }
        Set-VoicemailState -Upn $userUpn -Settings $requestedSettings
        return (Write-StageResult -Summary "Updated voicemail settings for $userUpn." -After ([ordered]@{ voicemailSettings = Get-VoicemailState -Upn $userUpn; changed = $true }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $matches = Test-SettingsMatch -Actual (Get-VoicemailState -Upn $userUpn) -Expected $requestedSettings
        $checks = @((New-Check -Check 'requested voicemail settings applied' -Passed $matches -Detail $(if ($matches) { 'All requested voicemail settings match.' } else { 'One or more requested voicemail settings do not match.' })))
        return (Write-StageResult -Summary $(if ($matches) { "Verified voicemail settings for $userUpn." } else { 'Voicemail settings verification failed.' }) -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'voicemailSettings'
        $restore = [ordered]@{}
        foreach ($name in $script:SettingMap.Keys) {
            if (Test-HasProperty -InputObject $requestedSettings -Name $name) {
                $restore[$name] = [bool](Get-PropertyValue -InputObject $original -Name $name)
            }
        }
        if (-not (Test-SettingsMatch -Actual (Get-VoicemailState -Upn $userUpn) -Expected $restore)) {
            Set-VoicemailState -Upn $userUpn -Settings $restore
        }
        return (Write-StageResult -Summary "Restored original voicemail settings for $userUpn." -After (Get-VoicemailState -Upn $userUpn))
    }

    default { throw "Tool 'update-user-voicemail-settings' does not implement stage '$Stage'." }
}