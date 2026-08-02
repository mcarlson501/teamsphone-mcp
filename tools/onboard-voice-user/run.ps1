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

$script:ToolsRoot = Split-Path $PSScriptRoot -Parent

function Test-HasProperty {
    param([AllowNull()][object]$InputObject, [string]$Name)
    if ($InputObject -is [System.Collections.IDictionary]) { return $InputObject.Contains($Name) }
    return $null -ne $InputObject -and $InputObject.PSObject.Properties.Name -contains $Name
}

function New-ChildInput {
    param([Parameter(Mandatory = $true)][object]$InputObject, [AllowNull()][object]$Snapshot)
    return (@{ input = $InputObject; snapshot = $Snapshot; pagination = $null } | ConvertTo-Json -Depth 32)
}

function Invoke-ChildStage {
    param(
        [Parameter(Mandatory = $true)][string]$ToolId,
        [Parameter(Mandatory = $true)][string]$ChildStage,
        [Parameter(Mandatory = $true)][object]$ChildInput,
        [AllowNull()][object]$ChildSnapshot
    )

    $scriptPath = Join-Path $script:ToolsRoot $ToolId 'run.ps1'
    return (& $scriptPath -Stage $ChildStage -InputJson (New-ChildInput -InputObject $ChildInput -Snapshot $ChildSnapshot) | ConvertFrom-Json -Depth 32)
}

function Get-AssignInput {
    param([Parameter(Mandatory = $true)][object]$InputObject)
    $result = [ordered]@{
        userUpn     = [string](Get-PropertyValue -InputObject $InputObject -Name 'userUpn')
        phoneNumber = [string](Get-PropertyValue -InputObject $InputObject -Name 'phoneNumber')
    }
    foreach ($name in @('onlineVoiceRoutingPolicy', 'tenantDialPlan', 'teamsCallingPolicy')) {
        $value = [string](Get-PropertyValue -InputObject $InputObject -Name $name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $result[$name] = $value }
    }
    $locationId = [string](Get-PropertyValue -InputObject $InputObject -Name 'emergencyLocationId')
    if (-not [string]::IsNullOrWhiteSpace($locationId)) { $result.locationId = $locationId }
    return $result
}

function Get-CallerIdInput {
    param([Parameter(Mandatory = $true)][object]$InputObject)
    $policy = [string](Get-PropertyValue -InputObject $InputObject -Name 'callerIdPolicy')
    if ([string]::IsNullOrWhiteSpace($policy)) { return $null }
    return [ordered]@{ userUpn = [string](Get-PropertyValue -InputObject $InputObject -Name 'userUpn'); policyName = $policy }
}

function Get-VoicemailInput {
    param([Parameter(Mandatory = $true)][object]$InputObject)
    $result = [ordered]@{ userUpn = [string](Get-PropertyValue -InputObject $InputObject -Name 'userUpn') }
    foreach ($name in @('voicemailEnabled', 'followAutomaticReplies', 'followCalendarEvents')) {
        if (Test-HasProperty -InputObject $InputObject -Name $name) { $result[$name] = [bool](Get-PropertyValue -InputObject $InputObject -Name $name) }
    }
    if ($result.Count -eq 1) { return $null }
    return $result
}

function Get-EmergencySnapshot {
    param([Parameter(Mandatory = $true)][string]$PhoneNumber, [Parameter(Mandatory = $true)][string]$LocationId)

    if (-not (Test-IsGuid -Value $LocationId)) {
        return [ordered]@{ requestedLocationId = $LocationId; exists = $false; validated = $false; phoneNumber = $PhoneNumber }
    }
    $locations = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineLisLocation -LocationId ([Guid]$LocationId) -ErrorAction Stop } | Where-Object { $null -ne $_ })
    $assignments = @(Invoke-WithRetry -ScriptBlock { Get-CsPhoneNumberAssignment -TelephoneNumber $PhoneNumber -ErrorAction Stop } | Where-Object { $null -ne $_ })
    $location = if ($locations.Count -gt 0) { $locations[0] } else { $null }
    $assignment = if ($assignments.Count -gt 0) { $assignments[0] } else { $null }
    return [ordered]@{
        phoneNumber          = $PhoneNumber
        phoneNumberType      = [string](Get-PropertyValue -InputObject $assignment -Name 'NumberType')
        assignmentStatus     = [string](Get-PropertyValue -InputObject $assignment -Name 'PstnAssignmentStatus')
        originalLocationId   = [string](Get-PropertyValue -InputObject $assignment -Name 'LocationId')
        requestedLocationId  = $LocationId
        exists               = $null -ne $location
        validated            = Test-TeamsEmergencyLocationValidated -Location $location
    }
}

function Test-EmergencyEligible {
    param([Parameter(Mandatory = $true)][object]$Emergency)
    $restorable = -not [string]::IsNullOrWhiteSpace([string]$Emergency.originalLocationId) -or
        [string]::Equals([string]$Emergency.phoneNumberType, 'DirectRouting', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals([string]$Emergency.assignmentStatus, 'Unassigned', [System.StringComparison]::OrdinalIgnoreCase)
    return [bool]$Emergency.exists -and [bool]$Emergency.validated -and $restorable
}

function Restore-Emergency {
    param([Parameter(Mandatory = $true)][object]$Emergency)
    if ([string]::Equals([string]$Emergency.assignmentStatus, 'Unassigned', [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::IsNullOrWhiteSpace([string]$Emergency.originalLocationId)) {
        return
    }
    $restoreId = if (-not [string]::IsNullOrWhiteSpace([string]$Emergency.originalLocationId)) {
        [string]$Emergency.originalLocationId
    } elseif ([string]::Equals([string]$Emergency.phoneNumberType, 'DirectRouting', [System.StringComparison]::OrdinalIgnoreCase)) {
        'null'
    } else { $null }
    if ($null -eq $restoreId) { throw 'The original emergency location cannot be restored automatically.' }
    $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -PhoneNumber $Emergency.phoneNumber -LocationId $restoreId -ErrorAction Stop }
}

function Test-ChildPassed {
    param([Parameter(Mandatory = $true)][object]$Result)
    return @($Result.checks | Where-Object { -not [bool]$_.passed }).Count -eq 0
}

function New-CompositeCheck {
    param([string]$Check, [bool]$Passed, [string]$Detail)
    return [ordered]@{ check = $Check; passed = $Passed; detail = $Detail }
}

function Assert-Snapshot {
    param([AllowNull()][object]$Snapshot, [string]$StageName)
    if ($null -eq $Snapshot) { throw "Stage '$StageName' requires the captured snapshot but none was supplied." }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$snapshot = Get-PropertyValue -InputObject $payload -Name 'snapshot'
$assignInput = Get-AssignInput -InputObject $toolInput
$callerInput = Get-CallerIdInput -InputObject $toolInput
$voicemailInput = Get-VoicemailInput -InputObject $toolInput
$emergencyLocationId = [string](Get-PropertyValue -InputObject $toolInput -Name 'emergencyLocationId')

switch ($Stage) {
    'snapshot' {
        $children = [ordered]@{
            assignPhoneNumber = [ordered]@{ input = $assignInput; snapshot = Invoke-ChildStage -ToolId 'assign-phone-number' -ChildStage 'snapshot' -ChildInput $assignInput -ChildSnapshot $null }
        }
        if ($null -ne $callerInput) {
            $children.callerId = [ordered]@{ input = $callerInput; snapshot = Invoke-ChildStage -ToolId 'set-caller-id-assignment' -ChildStage 'snapshot' -ChildInput $callerInput -ChildSnapshot $null }
        }
        if ($null -ne $voicemailInput) {
            $children.voicemail = [ordered]@{ input = $voicemailInput; snapshot = Invoke-ChildStage -ToolId 'update-user-voicemail-settings' -ChildStage 'snapshot' -ChildInput $voicemailInput -ChildSnapshot $null }
        }
        if (-not [string]::IsNullOrWhiteSpace($emergencyLocationId)) {
            $children.emergency = Get-EmergencySnapshot -PhoneNumber ([string]$children.assignPhoneNumber.snapshot.phoneNumber) -LocationId $emergencyLocationId
        }
        return (Write-StageSnapshot -State ([ordered]@{ children = $children; capturedAt = (Get-Date).ToUniversalTime().ToString('o') }))
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $children = Get-PropertyValue -InputObject $snapshot -Name 'children'
        $assign = Invoke-ChildStage -ToolId 'assign-phone-number' -ChildStage 'preflight' -ChildInput $assignInput -ChildSnapshot $children.assignPhoneNumber.snapshot
        $callerPassed = $true
        $callerDetail = 'Caller ID assignment was not requested.'
        if ($null -ne $callerInput) {
            $caller = Invoke-ChildStage -ToolId 'set-caller-id-assignment' -ChildStage 'preflight' -ChildInput $callerInput -ChildSnapshot $children.callerId.snapshot
            $callerPassed = Test-ChildPassed -Result $caller
            $callerDetail = [string]$caller.summary
        }
        $voicemailPassed = $true
        $voicemailDetail = 'Voicemail settings were not requested.'
        if ($null -ne $voicemailInput) {
            $voicemail = Invoke-ChildStage -ToolId 'update-user-voicemail-settings' -ChildStage 'preflight' -ChildInput $voicemailInput -ChildSnapshot $children.voicemail.snapshot
            $voicemailPassed = Test-ChildPassed -Result $voicemail
            $voicemailDetail = [string]$voicemail.summary
        }
        $emergencyPassed = $true
        $emergencyDetail = 'Emergency location was not requested.'
        if (-not [string]::IsNullOrWhiteSpace($emergencyLocationId)) {
            $emergencyPassed = Test-EmergencyEligible -Emergency $children.emergency
            $emergencyDetail = if ($emergencyPassed) { 'The requested emergency location is validated and rollback-safe.' } else { 'The requested emergency location is missing, unvalidated, or not rollback-safe.' }
        }
        $checks = @(
            (New-CompositeCheck -Check 'phone number assignment preflight passed' -Passed (Test-ChildPassed -Result $assign) -Detail ([string]$assign.summary)),
            (New-CompositeCheck -Check 'caller ID assignment preflight passed' -Passed $callerPassed -Detail $callerDetail),
            (New-CompositeCheck -Check 'voicemail settings preflight passed' -Passed $voicemailPassed -Detail $voicemailDetail),
            (New-CompositeCheck -Check 'emergency location preflight passed' -Passed $emergencyPassed -Detail $emergencyDetail)
        )
        $failed = @($checks | Where-Object { -not $_.passed }).Count
        return (Write-StageResult -Summary $(if ($failed -eq 0) { 'All onboarding preflight checks passed.' } else { "$failed onboarding preflight step(s) failed." }) -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $children = Get-PropertyValue -InputObject $snapshot -Name 'children'
        $commands = @()
        $steps = @()
        foreach ($definition in @(
            @{ key = 'assignPhoneNumber'; tool = 'assign-phone-number'; input = $assignInput },
            @{ key = 'callerId'; tool = 'set-caller-id-assignment'; input = $callerInput },
            @{ key = 'voicemail'; tool = 'update-user-voicemail-settings'; input = $voicemailInput }
        )) {
            if ($null -eq $definition.input) { continue }
            $child = Get-PropertyValue -InputObject $children -Name $definition.key
            $result = Invoke-ChildStage -ToolId $definition.tool -ChildStage 'dryrun' -ChildInput $definition.input -ChildSnapshot $child.snapshot
            $commands += @(Get-PropertyValue -InputObject $result.after -Name 'plannedCommands' -Default @())
            $steps += [ordered]@{ step = $definition.key; status = 'planned'; summary = [string]$result.summary }
        }
        if (-not [string]::IsNullOrWhiteSpace($emergencyLocationId)) {
            $commands += "Set-CsPhoneNumberAssignment -PhoneNumber '$($children.emergency.phoneNumber)' -LocationId '$emergencyLocationId'"
            $steps += [ordered]@{ step = 'emergency'; status = 'planned'; summary = 'Emergency location assignment planned.' }
        }
        return (Write-StageResult -Summary "Would run $($steps.Count) onboarding step(s)." -After ([ordered]@{ steps = $steps; plannedCommands = $commands }))
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $children = Get-PropertyValue -InputObject $snapshot -Name 'children'
        $completed = [System.Collections.Generic.List[string]]::new()
        $steps = @()
        try {
            $result = Invoke-ChildStage -ToolId 'assign-phone-number' -ChildStage 'execute' -ChildInput $assignInput -ChildSnapshot $children.assignPhoneNumber.snapshot
            $completed.Add('assignPhoneNumber')
            $steps += [ordered]@{ step = 'assignPhoneNumber'; status = 'succeeded'; summary = [string]$result.summary }

            if ($null -ne $callerInput) {
                $result = Invoke-ChildStage -ToolId 'set-caller-id-assignment' -ChildStage 'execute' -ChildInput $callerInput -ChildSnapshot $children.callerId.snapshot
                $completed.Add('callerId')
                $steps += [ordered]@{ step = 'callerId'; status = 'succeeded'; summary = [string]$result.summary }
            }
            if ($null -ne $voicemailInput) {
                $result = Invoke-ChildStage -ToolId 'update-user-voicemail-settings' -ChildStage 'execute' -ChildInput $voicemailInput -ChildSnapshot $children.voicemail.snapshot
                $completed.Add('voicemail')
                $steps += [ordered]@{ step = 'voicemail'; status = 'succeeded'; summary = [string]$result.summary }
            }
            if (-not [string]::IsNullOrWhiteSpace($emergencyLocationId)) {
                $fresh = Get-EmergencySnapshot -PhoneNumber ([string]$children.emergency.phoneNumber) -LocationId $emergencyLocationId
                if (-not $fresh.exists -or -not $fresh.validated) { throw 'The requested emergency location is no longer valid.' }
                if (-not [string]::Equals([string]$fresh.originalLocationId, $emergencyLocationId, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -PhoneNumber $fresh.phoneNumber -LocationId $emergencyLocationId -ErrorAction Stop }
                }
                $completed.Add('emergency')
                $steps += [ordered]@{ step = 'emergency'; status = 'succeeded'; summary = 'Emergency location assigned.' }
            }
        } catch {
            $originalError = $_
            try {
                if ($completed.Contains('emergency')) { Restore-Emergency -Emergency $children.emergency }
                if ($completed.Contains('voicemail')) { $null = Invoke-ChildStage -ToolId 'update-user-voicemail-settings' -ChildStage 'rollback' -ChildInput $voicemailInput -ChildSnapshot $children.voicemail.snapshot }
                if ($completed.Contains('callerId')) { $null = Invoke-ChildStage -ToolId 'set-caller-id-assignment' -ChildStage 'rollback' -ChildInput $callerInput -ChildSnapshot $children.callerId.snapshot }
                if ($completed.Contains('assignPhoneNumber')) { $null = Invoke-ChildStage -ToolId 'assign-phone-number' -ChildStage 'rollback' -ChildInput $assignInput -ChildSnapshot $children.assignPhoneNumber.snapshot }
            } catch { throw 'Onboarding failed and compensation also failed; manual intervention is required.' }
            throw $originalError
        }
        return (Write-StageResult -Summary "Onboarded $($assignInput.userUpn) for Teams Phone." -After ([ordered]@{ steps = $steps; completedStepCount = $steps.Count }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $children = Get-PropertyValue -InputObject $snapshot -Name 'children'
        $assign = Invoke-ChildStage -ToolId 'assign-phone-number' -ChildStage 'verify' -ChildInput $assignInput -ChildSnapshot $children.assignPhoneNumber.snapshot
        $callerPassed = $true
        if ($null -ne $callerInput) {
            $callerPassed = Test-ChildPassed -Result (Invoke-ChildStage -ToolId 'set-caller-id-assignment' -ChildStage 'verify' -ChildInput $callerInput -ChildSnapshot $children.callerId.snapshot)
        }
        $voicemailPassed = $true
        if ($null -ne $voicemailInput) {
            $voicemailPassed = Test-ChildPassed -Result (Invoke-ChildStage -ToolId 'update-user-voicemail-settings' -ChildStage 'verify' -ChildInput $voicemailInput -ChildSnapshot $children.voicemail.snapshot)
        }
        $emergencyPassed = $true
        if (-not [string]::IsNullOrWhiteSpace($emergencyLocationId)) {
            $fresh = Get-EmergencySnapshot -PhoneNumber ([string]$children.emergency.phoneNumber) -LocationId $emergencyLocationId
            $emergencyPassed = [string]::Equals([string]$fresh.originalLocationId, $emergencyLocationId, [System.StringComparison]::OrdinalIgnoreCase)
        }
        $checks = @(
            (New-CompositeCheck -Check 'phone number assignment verified' -Passed (Test-ChildPassed -Result $assign) -Detail ([string]$assign.summary)),
            (New-CompositeCheck -Check 'caller ID assignment verified' -Passed $callerPassed -Detail $(if ($null -eq $callerInput) { 'Not requested.' } else { 'Caller ID verification completed.' })),
            (New-CompositeCheck -Check 'voicemail settings verified' -Passed $voicemailPassed -Detail $(if ($null -eq $voicemailInput) { 'Not requested.' } else { 'Voicemail verification completed.' })),
            (New-CompositeCheck -Check 'emergency location assignment verified' -Passed $emergencyPassed -Detail $(if ([string]::IsNullOrWhiteSpace($emergencyLocationId)) { 'Not requested.' } else { 'Emergency location verification completed.' }))
        )
        return (Write-StageResult -Summary 'Completed onboarding verification.' -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $children = Get-PropertyValue -InputObject $snapshot -Name 'children'
        if (-not [string]::IsNullOrWhiteSpace($emergencyLocationId)) { Restore-Emergency -Emergency $children.emergency }
        if ($null -ne $voicemailInput) { $null = Invoke-ChildStage -ToolId 'update-user-voicemail-settings' -ChildStage 'rollback' -ChildInput $voicemailInput -ChildSnapshot $children.voicemail.snapshot }
        if ($null -ne $callerInput) { $null = Invoke-ChildStage -ToolId 'set-caller-id-assignment' -ChildStage 'rollback' -ChildInput $callerInput -ChildSnapshot $children.callerId.snapshot }
        $null = Invoke-ChildStage -ToolId 'assign-phone-number' -ChildStage 'rollback' -ChildInput $assignInput -ChildSnapshot $children.assignPhoneNumber.snapshot
        return (Write-StageResult -Summary "Rolled back Teams Phone onboarding for $($assignInput.userUpn)." -After ([ordered]@{ restored = $true }))
    }

    default { throw "Tool 'onboard-voice-user' does not implement stage '$Stage'." }
}