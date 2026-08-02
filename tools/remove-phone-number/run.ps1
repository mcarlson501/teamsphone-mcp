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
        userPrincipalName      = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        objectId               = if ($null -ne $objectId) { [string]$objectId } else { $null }
        accountType            = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
        enterpriseVoiceEnabled = [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)
        lineUri                = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
    }
}

function Get-NumberAssignment {
    param([AllowNull()][string]$Number)

    if ([string]::IsNullOrWhiteSpace($Number)) { return $null }
    $assignments = @(Invoke-WithRetry -ScriptBlock {
        Get-CsPhoneNumberAssignment -TelephoneNumber $Number -ErrorAction Stop
    } | Where-Object { $null -ne $_ })

    if ($assignments.Count -eq 0) { return $null }
    return $assignments[0]
}

function New-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Check,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [AllowNull()][string]$Detail
    )

    return [ordered]@{ check = $Check; passed = $Passed; detail = $Detail }
}

function Get-SnapshotValue {
    param(
        [AllowNull()][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$Name,
        [object]$Default = $null
    )

    return Get-PropertyValue -InputObject $Snapshot -Name $Name -Default $Default
}

function Assert-Snapshot {
    param([AllowNull()][object]$Snapshot, [Parameter(Mandatory = $true)][string]$StageName)

    if ($null -eq $Snapshot) {
        throw "Stage '$StageName' requires the captured snapshot but none was supplied."
    }
}

function Test-AssignmentTargetsUser {
    param([AllowNull()][object]$Assignment, [Parameter(Mandatory = $true)][object]$User)

    if ($null -eq $Assignment) { return $false }
    $target = [string](Get-PropertyValue -InputObject $Assignment -Name 'AssignedPstnTargetId')
    if ([string]::IsNullOrWhiteSpace($target)) { return $false }

    return [string]::Equals($target, [string]$User.objectId, [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($target, [string]$User.userPrincipalName, [System.StringComparison]::OrdinalIgnoreCase)
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$snapshot = Get-PropertyValue -InputObject $payload -Name 'snapshot'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')
$requestedNumberRaw = [string](Get-PropertyValue -InputObject $toolInput -Name 'phoneNumber')

switch ($Stage) {
    'snapshot' {
        $user = Get-UserState -Upn $userUpn
        $requestedNumber = if ([string]::IsNullOrWhiteSpace($requestedNumberRaw)) { $null } else { ConvertTo-E164Number -Value $requestedNumberRaw }
        if (-not [string]::IsNullOrWhiteSpace($requestedNumberRaw) -and $null -eq $requestedNumber) {
            throw 'The supplied phoneNumber is not a valid E.164 number.'
        }

        $number = if ($null -ne $requestedNumber) { $requestedNumber } else { $user.lineUri }
        $assignment = Get-NumberAssignment -Number $number
        $state = [ordered]@{
            phoneNumber        = $number
            phoneNumberType    = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'NumberType') } else { $null }
            assignmentStatus   = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'PstnAssignmentStatus') } else { $null }
            assignedPstnTarget = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'AssignedPstnTargetId') } else { $null }
            locationId         = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'LocationId') } else { $null }
            user               = $user
            capturedAt         = (Get-Date).ToUniversalTime().ToString('o')
        }

        return (Write-StageSnapshot -State $state)
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $user = Get-SnapshotValue -Snapshot $snapshot -Name 'user'
        $accountType = [string](Get-PropertyValue -InputObject $user -Name 'accountType')
        $isUser = [string]::IsNullOrWhiteSpace($accountType) -or [string]::Equals($accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)
        $holdsNumber = -not [string]::IsNullOrWhiteSpace($number) -and [string]::Equals([string]$user.lineUri, $number, [System.StringComparison]::OrdinalIgnoreCase)
        $typeKnown = -not [string]::IsNullOrWhiteSpace($numberType)
        $inventoryTarget = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'assignedPstnTarget')
        $inventoryMatches = -not [string]::IsNullOrWhiteSpace($inventoryTarget) -and (
            [string]::Equals($inventoryTarget, [string]$user.objectId, [System.StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($inventoryTarget, [string]$user.userPrincipalName, [System.StringComparison]::OrdinalIgnoreCase)
        )

        $checks = @(
            (New-Check -Check 'target is a user account' -Passed $isUser -Detail $(if ($isUser) { "$userUpn is a user account." } else { "$userUpn is a $accountType." })),
            (New-Check -Check 'target user has the phone number assigned' -Passed $holdsNumber -Detail $(if ($holdsNumber) { "$userUpn holds $number." } else { "$userUpn does not hold the requested number." })),
            (New-Check -Check 'the number has a known tenant number type' -Passed $typeKnown -Detail $(if ($typeKnown) { "Number type is $numberType." } else { 'The number type is unknown.' })),
            (New-Check -Check 'tenant inventory assigns the number to the target user' -Passed $inventoryMatches -Detail $(if ($inventoryMatches) { "Tenant inventory assigns $number to $userUpn." } else { 'Tenant inventory does not match the target user.' }))
        )

        $failed = @($checks | Where-Object { -not $_.passed }).Count
        $summary = if ($failed -eq 0) { "All preflight checks passed for removing $number from $userUpn." } else { "$failed preflight check(s) failed; the number was not removed." }
        return (Write-StageResult -Summary $summary -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $after = [ordered]@{
            userPrincipalName      = $userUpn
            lineUri                = $null
            enterpriseVoiceEnabled = [bool](Get-PropertyValue -InputObject (Get-SnapshotValue -Snapshot $snapshot -Name 'user') -Name 'enterpriseVoiceEnabled')
            phoneNumber            = $number
            phoneNumberType        = $numberType
            plannedCommands        = @("Remove-CsPhoneNumberAssignment -Identity '$userUpn' -PhoneNumber '$number' -PhoneNumberType $numberType")
        }

        return (Write-StageResult -Summary "Would remove $number from $userUpn and release it to inventory." -After $after)
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        if ([string]::IsNullOrWhiteSpace($number) -or [string]::IsNullOrWhiteSpace($numberType)) {
            throw 'The snapshot does not contain a removable number and type; nothing was changed.'
        }

        $live = Get-UserState -Upn $userUpn
        if ([string]::IsNullOrWhiteSpace($live.lineUri)) {
            return (Write-StageResult -Summary "$userUpn has no phone number assigned; no change was needed." -After ([ordered]@{ user = $live; phoneNumber = $number; phoneNumberType = $numberType; changed = $false }))
        }
        if ($live.lineUri -ne $number) {
            throw "The tenant state changed since the snapshot: $userUpn now holds a different number. Nothing was changed."
        }

        $assignment = Get-NumberAssignment -Number $number
        if (-not (Test-AssignmentTargetsUser -Assignment $assignment -User $live)) {
            throw "The tenant inventory changed since the snapshot and no longer assigns $number to $userUpn. Nothing was changed."
        }

        $null = Invoke-WithRetry -ScriptBlock {
            Remove-CsPhoneNumberAssignment -Identity $userUpn -PhoneNumber $number -PhoneNumberType $numberType -ErrorAction Stop
        }

        return (Write-StageResult -Summary "Removed $number from $userUpn and released it to inventory." -After ([ordered]@{ user = Get-UserState -Upn $userUpn; phoneNumber = $number; phoneNumberType = $numberType; changed = $true }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $released = Wait-ForCondition -TimeoutSeconds $script:VerifyTimeoutSeconds -PollIntervalSeconds $script:VerifyPollSeconds -Condition {
            [string]::IsNullOrWhiteSpace((Get-UserState -Upn $userUpn).lineUri)
        }
        $assignment = Get-NumberAssignment -Number $number
        $inventoryReleased = $null -ne $assignment -and
            [string]::Equals([string](Get-PropertyValue -InputObject $assignment -Name 'PstnAssignmentStatus'), 'Unassigned', [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::IsNullOrWhiteSpace([string](Get-PropertyValue -InputObject $assignment -Name 'AssignedPstnTargetId'))

        $checks = @(
            (New-Check -Check 'number released from target user' -Passed $released -Detail $(if ($released) { "$userUpn no longer holds $number." } else { "$userUpn still holds $number." })),
            (New-Check -Check 'number released to tenant inventory' -Passed $inventoryReleased -Detail $(if ($inventoryReleased) { "$number is unassigned in tenant inventory." } else { "$number is not unassigned in tenant inventory." }))
        )

        $failed = @($checks | Where-Object { -not $_.passed }).Count
        $summary = if ($failed -eq 0) { "Verified $number was released from $userUpn to tenant inventory." } else { "$failed verification check(s) failed after removing the number." }
        return (Write-StageResult -Summary $summary -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $original = Get-SnapshotValue -Snapshot $snapshot -Name 'user'
        $originalHeldNumber = [string]::Equals([string](Get-PropertyValue -InputObject $original -Name 'lineUri'), $number, [System.StringComparison]::OrdinalIgnoreCase)
        if (-not $originalHeldNumber -or [string]::IsNullOrWhiteSpace($numberType)) {
            return (Write-StageResult -Summary 'No phone number assignment needed to be restored.' -After ([ordered]@{ restored = $false }))
        }

        $live = Get-UserState -Upn $userUpn
        if ([string]::IsNullOrWhiteSpace($live.lineUri)) {
            $assignment = Get-NumberAssignment -Number $number
            $status = [string](Get-PropertyValue -InputObject $assignment -Name 'PstnAssignmentStatus')
            $target = [string](Get-PropertyValue -InputObject $assignment -Name 'AssignedPstnTargetId')
            if (-not [string]::Equals($status, 'Unassigned', [System.StringComparison]::OrdinalIgnoreCase) -or -not [string]::IsNullOrWhiteSpace($target)) {
                throw "$number cannot be restored because it is no longer unassigned; manual intervention is required."
            }

            $null = Invoke-WithRetry -ScriptBlock {
                Set-CsPhoneNumberAssignment -Identity $userUpn -PhoneNumber $number -PhoneNumberType $numberType -ErrorAction Stop
            }
            $live = Get-UserState -Upn $userUpn
        } elseif ($live.lineUri -ne $number) {
            throw "$userUpn now holds a different number; automatic rollback was not attempted."
        }

        return (Write-StageResult -Summary "Restored $number to $userUpn." -After ([ordered]@{ user = $live; phoneNumber = $number; phoneNumberType = $numberType; restored = $true }))
    }

    default {
        throw "Tool 'remove-phone-number' does not implement stage '$Stage'."
    }
}