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

$script:VerifyTimeoutSeconds = 120
$script:VerifyPollSeconds = 5

function Get-UserState {
    param([Parameter(Mandatory = $true)][string]$Upn)

    $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    if ($null -eq $user) { throw "User '$Upn' was not found in the tenant." }
    $objectId = Get-PropertyValue -InputObject $user -Name 'Identity'
    if ($null -eq $objectId) { $objectId = Get-PropertyValue -InputObject $user -Name 'ObjectId' }
    return [ordered]@{
        userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        objectId          = if ($null -ne $objectId) { [string]$objectId } else { $null }
        accountType       = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
        lineUri           = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
    }
}

function Get-NumberState {
    param([AllowNull()][string]$Number)

    if ([string]::IsNullOrWhiteSpace($Number)) { return $null }
    $assignments = @(Invoke-WithRetry -ScriptBlock { Get-CsPhoneNumberAssignment -TelephoneNumber $Number -ErrorAction Stop } | Where-Object { $null -ne $_ })
    if ($assignments.Count -eq 0) { return $null }
    $assignment = $assignments[0]
    return [ordered]@{
        phoneNumber       = $Number
        phoneNumberType   = [string](Get-PropertyValue -InputObject $assignment -Name 'NumberType')
        locationId        = [string](Get-PropertyValue -InputObject $assignment -Name 'LocationId')
        assignmentStatus  = [string](Get-PropertyValue -InputObject $assignment -Name 'PstnAssignmentStatus')
        assignedPstnTarget = [string](Get-PropertyValue -InputObject $assignment -Name 'AssignedPstnTargetId')
    }
}

function Get-LocationState {
    param([Parameter(Mandatory = $true)][string]$Id)

    if (-not (Test-IsGuid -Value $Id)) {
        return [ordered]@{ locationId = $Id; exists = $false; validated = $false }
    }
    $locations = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineLisLocation -LocationId ([Guid]$Id) -ErrorAction Stop } | Where-Object { $null -ne $_ })
    if ($locations.Count -eq 0) {
        return [ordered]@{ locationId = $Id; exists = $false; validated = $false }
    }
    $location = $locations[0]
    return [ordered]@{
        locationId      = [string](Get-PropertyValue -InputObject $location -Name 'LocationId' -Default $Id)
        civicAddressId  = [string](Get-PropertyValue -InputObject $location -Name 'CivicAddressId')
        description     = [string](Get-PropertyValue -InputObject $location -Name 'Description')
        city            = [string](Get-PropertyValue -InputObject $location -Name 'City')
        countryOrRegion = [string](Get-PropertyValue -InputObject $location -Name 'CountryOrRegion')
        exists          = $true
        validated       = Test-TeamsEmergencyLocationValidated -Location $location
    }
}

function Test-AssignmentTargetsUser {
    param([AllowNull()][object]$NumberState, [Parameter(Mandatory = $true)][object]$User)
    if ($null -eq $NumberState) { return $false }
    $target = [string](Get-PropertyValue -InputObject $NumberState -Name 'assignedPstnTarget')
    return -not [string]::IsNullOrWhiteSpace($target) -and (
        [string]::Equals($target, [string]$User.objectId, [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($target, [string]$User.userPrincipalName, [System.StringComparison]::OrdinalIgnoreCase)
    )
}

function Test-OriginalLocationRestorable {
    param([AllowNull()][object]$NumberState)
    if ($null -eq $NumberState) { return $false }
    $locationId = [string](Get-PropertyValue -InputObject $NumberState -Name 'locationId')
    $numberType = [string](Get-PropertyValue -InputObject $NumberState -Name 'phoneNumberType')
    return -not [string]::IsNullOrWhiteSpace($locationId) -or
        [string]::Equals($numberType, 'DirectRouting', [System.StringComparison]::OrdinalIgnoreCase)
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
$requestedLocationId = [string](Get-PropertyValue -InputObject $toolInput -Name 'locationId')

switch ($Stage) {
    'snapshot' {
        $user = Get-UserState -Upn $userUpn
        return (Write-StageSnapshot -State ([ordered]@{
            user              = $user
            number            = Get-NumberState -Number $user.lineUri
            requestedLocation = Get-LocationState -Id $requestedLocationId
            capturedAt        = (Get-Date).ToUniversalTime().ToString('o')
        }))
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $number = Get-PropertyValue -InputObject $snapshot -Name 'number'
        $location = Get-PropertyValue -InputObject $snapshot -Name 'requestedLocation'
        $accountType = [string](Get-PropertyValue -InputObject $user -Name 'accountType')
        $targetReady = ([string]::IsNullOrWhiteSpace($accountType) -or [string]::Equals($accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)) -and
            -not [string]::IsNullOrWhiteSpace([string](Get-PropertyValue -InputObject $user -Name 'lineUri'))
        $inventoryMatches = Test-AssignmentTargetsUser -NumberState $number -User $user
        $locationExists = [bool](Get-PropertyValue -InputObject $location -Name 'exists' -Default $false)
        $locationValidated = [bool](Get-PropertyValue -InputObject $location -Name 'validated' -Default $false)
        $numberType = [string](Get-PropertyValue -InputObject $number -Name 'phoneNumberType')
        $restorable = Test-OriginalLocationRestorable -NumberState $number

        $checks = @(
            (New-Check -Check 'target is a user account with a phone number' -Passed $targetReady -Detail $(if ($targetReady) { "$userUpn is a user with phone number $($user.lineUri)." } else { "$userUpn is not an eligible numbered user." })),
            (New-Check -Check 'tenant inventory assigns the number to the target user' -Passed $inventoryMatches -Detail $(if ($inventoryMatches) { 'Tenant inventory matches the target user.' } else { 'Tenant inventory does not match the target user.' })),
            (New-Check -Check 'requested emergency location exists' -Passed $locationExists -Detail $(if ($locationExists) { "Emergency location '$requestedLocationId' exists." } else { "Emergency location '$requestedLocationId' was not found." })),
            (New-Check -Check 'requested emergency location is validated' -Passed $locationValidated -Detail $(if ($locationValidated) { 'The requested emergency location is validated.' } else { 'The requested emergency location is not validated.' })),
            (New-Check -Check 'original emergency location can be restored' -Passed $restorable -Detail $(if ($restorable) { 'The original emergency location state is restorable.' } else { "$numberType does not support safe removal of a newly assigned location." }))
        )
        $failed = @($checks | Where-Object { -not $_.passed }).Count
        return (Write-StageResult -Summary $(if ($failed -eq 0) { "All preflight checks passed for updating $userUpn emergency location." } else { "$failed preflight check(s) failed; emergency location was not updated." }) -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $number = Get-PropertyValue -InputObject $snapshot -Name 'number'
        $location = Get-PropertyValue -InputObject $snapshot -Name 'requestedLocation'
        return (Write-StageResult -Summary "Would assign emergency location '$requestedLocationId' to $userUpn phone number $($user.lineUri)." -After ([ordered]@{
            userPrincipalName = $userUpn
            phoneNumber       = [string]$user.lineUri
            phoneNumberType   = [string]$number.phoneNumberType
            location          = $location
            plannedCommands   = @("Set-CsPhoneNumberAssignment -PhoneNumber '$($user.lineUri)' -LocationId '$requestedLocationId'")
        }))
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $originalUser = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $originalNumber = Get-PropertyValue -InputObject $snapshot -Name 'number'
        if (-not (Test-OriginalLocationRestorable -NumberState $originalNumber)) {
            throw 'The original emergency location cannot be safely restored; nothing was changed.'
        }
        $liveUser = Get-UserState -Upn $userUpn
        if (-not [string]::Equals([string]$liveUser.lineUri, [string]$originalUser.lineUri, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'The user phone number changed since the snapshot; nothing was changed.'
        }
        $liveNumber = Get-NumberState -Number $liveUser.lineUri
        if (-not (Test-AssignmentTargetsUser -NumberState $liveNumber -User $liveUser)) {
            throw 'Tenant number inventory no longer matches the target user; nothing was changed.'
        }
        $liveLocation = Get-LocationState -Id $requestedLocationId
        if (-not $liveLocation.exists -or -not $liveLocation.validated) {
            throw 'The requested emergency location no longer exists as a validated location; nothing was changed.'
        }

        $originalLocationId = [string](Get-PropertyValue -InputObject $originalNumber -Name 'locationId')
        if (-not [string]::Equals([string]$liveNumber.locationId, $originalLocationId, [System.StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals([string]$liveNumber.locationId, $requestedLocationId, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'The emergency location changed since the snapshot; nothing was changed.'
        }
        if ([string]::Equals([string]$liveNumber.locationId, $requestedLocationId, [System.StringComparison]::OrdinalIgnoreCase)) {
            return (Write-StageResult -Summary "$userUpn phone number already has emergency location '$requestedLocationId'." -After ([ordered]@{ number = $liveNumber; changed = $false }))
        }

        $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -PhoneNumber $liveUser.lineUri -LocationId $requestedLocationId -ErrorAction Stop }
        return (Write-StageResult -Summary "Assigned emergency location '$requestedLocationId' to $userUpn." -After ([ordered]@{ number = Get-NumberState -Number $liveUser.lineUri; location = $liveLocation; changed = $true }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $assigned = Wait-ForCondition -TimeoutSeconds $script:VerifyTimeoutSeconds -PollIntervalSeconds $script:VerifyPollSeconds -Condition {
            [string]::Equals([string](Get-NumberState -Number ([string]$user.lineUri)).locationId, $requestedLocationId, [System.StringComparison]::OrdinalIgnoreCase)
        }
        $checks = @((New-Check -Check 'requested emergency location assigned to the user phone number' -Passed $assigned -Detail $(if ($assigned) { "$userUpn phone number has emergency location '$requestedLocationId'." } else { "$userUpn phone number does not have emergency location '$requestedLocationId'." })))
        return (Write-StageResult -Summary $(if ($assigned) { "Verified emergency location '$requestedLocationId' on $userUpn." } else { 'Emergency location verification failed.' }) -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $number = Get-PropertyValue -InputObject $snapshot -Name 'number'
        $originalLocationId = [string](Get-PropertyValue -InputObject $number -Name 'locationId')
        $numberType = [string](Get-PropertyValue -InputObject $number -Name 'phoneNumberType')
        $restoreLocationId = if (-not [string]::IsNullOrWhiteSpace($originalLocationId)) { $originalLocationId } elseif ([string]::Equals($numberType, 'DirectRouting', [System.StringComparison]::OrdinalIgnoreCase)) { 'null' } else { $null }
        if ($null -eq $restoreLocationId) { throw 'The original emergency location cannot be safely restored; manual intervention is required.' }

        $liveUser = Get-UserState -Upn $userUpn
        if (-not [string]::Equals([string]$liveUser.lineUri, [string]$user.lineUri, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'The user phone number changed; automatic emergency location rollback was not attempted.'
        }
        $liveNumber = Get-NumberState -Number $liveUser.lineUri
        if (-not [string]::Equals([string]$liveNumber.locationId, $originalLocationId, [System.StringComparison]::OrdinalIgnoreCase)) {
            $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -PhoneNumber $liveUser.lineUri -LocationId $restoreLocationId -ErrorAction Stop }
        }
        return (Write-StageResult -Summary "Restored the original emergency location on $userUpn phone number." -After (Get-NumberState -Number $liveUser.lineUri))
    }

    default { throw "Tool 'update-user-emergency-location' does not implement stage '$Stage'." }
}