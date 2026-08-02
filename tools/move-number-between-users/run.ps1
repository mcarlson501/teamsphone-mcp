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

# Bounded polling budget for the verify stage: number assignment is eventually
# consistent, so a fresh read right after the write can legitimately lag.
$script:VerifyTimeoutSeconds = 120
$script:VerifyPollSeconds = 5

function Get-UserState {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Upn)

    $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    if ($null -eq $user) {
        throw "User '$Upn' was not found in the tenant."
    }

    $objectId = Get-PropertyValue -InputObject $user -Name 'Identity'
    if ($null -eq $objectId) { $objectId = Get-PropertyValue -InputObject $user -Name 'ObjectId' }

    return [ordered]@{
        userPrincipalName        = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        objectId                 = if ($null -ne $objectId) { [string]$objectId } else { $null }
        accountType              = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
        enterpriseVoiceEnabled   = [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)
        lineUri                  = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
        featureTypes             = @(Get-PropertyValue -InputObject $user -Name 'FeatureTypes' -Default @())
        onlineVoiceRoutingPolicy = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'OnlineVoiceRoutingPolicy')
    }
}

function Get-NumberAssignment {
    [CmdletBinding()]
    param([AllowNull()][string]$Number)

    if ([string]::IsNullOrWhiteSpace($Number)) { return $null }

    # A number outside tenant inventory (common for Direct Routing) is not an
    # error here: preflight decides whether the missing type blocks the move.
    $assignment = $null
    try {
        $assignment = Invoke-WithRetry -ScriptBlock { Get-CsPhoneNumberAssignment -TelephoneNumber $Number -ErrorAction Stop }
    }
    catch {
        return $null
    }

    $candidates = @($assignment | Where-Object { $null -ne $_ })
    if ($candidates.Count -eq 0) { return $null }

    return $candidates[0]
}

function New-Check {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Check,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [AllowNull()][string]$Detail
    )

    return [ordered]@{
        check  = $Check
        passed = $Passed
        detail = $Detail
    }
}

function Get-SnapshotValue {
    [CmdletBinding()]
    param(
        [AllowNull()][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$Name,
        [object]$Default = $null
    )

    return Get-PropertyValue -InputObject $Snapshot -Name $Name -Default $Default
}

function Assert-Snapshot {
    [CmdletBinding()]
    param([AllowNull()][object]$Snapshot, [Parameter(Mandatory = $true)][string]$StageName)

    if ($null -eq $Snapshot) {
        throw "Stage '$StageName' requires the captured snapshot but none was supplied."
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = $payload.input
$snapshot = Get-PropertyValue -InputObject $payload -Name 'snapshot'

$sourceUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'sourceUserUpn')
$targetUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'targetUserUpn')
$requestedNumberRaw = [string](Get-PropertyValue -InputObject $toolInput -Name 'phoneNumber')

switch ($Stage) {
    'snapshot' {
        $requestedNumber = $null
        if (-not [string]::IsNullOrWhiteSpace($requestedNumberRaw)) {
            $requestedNumber = ConvertTo-E164Number -Value $requestedNumberRaw
            if ($null -eq $requestedNumber) {
                throw "The supplied phoneNumber is not a valid E.164 number."
            }
        }

        $source = Get-UserState -Upn $sourceUpn
        $target = Get-UserState -Upn $targetUpn

        $number = $requestedNumber
        if ($null -eq $number) { $number = $source.lineUri }

        $assignment = Get-NumberAssignment -Number $number

        $state = [ordered]@{
            phoneNumber        = $number
            phoneNumberType    = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'NumberType') } else { $null }
            assignmentStatus   = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'PstnAssignmentStatus') } else { $null }
            assignedPstnTarget = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'AssignedPstnTargetId') } else { $null }
            source             = $source
            target             = $target
            capturedAt         = (Get-Date).ToUniversalTime().ToString('o')
        }

        return (Write-StageSnapshot -State $state)
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber'
        $numberType = Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType'
        $source = Get-SnapshotValue -Snapshot $snapshot -Name 'source'
        $target = Get-SnapshotValue -Snapshot $snapshot -Name 'target'

        $sourceLineUri = Get-PropertyValue -InputObject $source -Name 'lineUri'
        $targetLineUri = Get-PropertyValue -InputObject $target -Name 'lineUri'
        $targetEnabled = [bool](Get-PropertyValue -InputObject $target -Name 'enterpriseVoiceEnabled' -Default $false)
        $targetFeatures = @(Get-PropertyValue -InputObject $target -Name 'featureTypes' -Default @())

        $distinct = -not [string]::Equals($sourceUpn, $targetUpn, [System.StringComparison]::OrdinalIgnoreCase)
        $sourceHasNumber = ($null -ne $number) -and ($sourceLineUri -eq $number)
        $typeKnown = -not [string]::IsNullOrWhiteSpace([string]$numberType)
        $targetFree = [string]::IsNullOrWhiteSpace([string]$targetLineUri)

        # Tenants differ in whether Get-CsOnlineUser reports licensing, so an
        # unknown license state is reported rather than treated as a failure —
        # the assignment cmdlet itself fails closed if the license is missing.
        $licenseReported = $targetFeatures.Count -gt 0
        $hasPhoneSystem = $targetEnabled -or ($targetFeatures -contains 'PhoneSystem')
        $voiceCapable = $hasPhoneSystem -or (-not $licenseReported)
        $voiceCapableDetail = if ($hasPhoneSystem) {
            "$targetUpn is enterprise-voice capable."
        }
        elseif ($licenseReported) {
            "$targetUpn has no Phone System license reported by the tenant."
        }
        else {
            "The tenant did not report license features for $targetUpn; the assignment will fail closed if the license is missing."
        }

        # Teams rejects a user number assigned to a resource account with an opaque
        # BadRequest, so the account type is checked before anything is changed.
        $targetAccountType = [string](Get-PropertyValue -InputObject $target -Name 'accountType')
        $accountTypeKnown = -not [string]::IsNullOrWhiteSpace($targetAccountType)
        $isUserAccount = (-not $accountTypeKnown) -or [string]::Equals($targetAccountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)
        $accountTypeDetail = if (-not $accountTypeKnown) {
            "The tenant did not report an account type for $targetUpn."
        }
        elseif ($isUserAccount) {
            "$targetUpn is a user account."
        }
        else {
            "$targetUpn is a $targetAccountType, which cannot hold a user phone number."
        }

        # A Calling Plan number needs the matching Calling Plan licence on the
        # target; Phone System alone is not enough.
        $isCallingPlan = [string]::Equals([string]$numberType, 'CallingPlan', [System.StringComparison]::OrdinalIgnoreCase)
        $planLicensed = (-not $isCallingPlan) -or (-not $licenseReported) -or ($targetFeatures -contains 'CallingPlan')
        $planDetail = if (-not $isCallingPlan) {
            "Number type $numberType does not require a Calling Plan licence."
        }
        elseif (-not $licenseReported) {
            "The tenant did not report license features for $targetUpn; the assignment will fail closed if the Calling Plan licence is missing."
        }
        elseif ($targetFeatures -contains 'CallingPlan') {
            "$targetUpn holds a Calling Plan licence."
        }
        else {
            "$targetUpn has no Calling Plan licence, which a $numberType number requires."
        }

        $checks = @(
            (New-Check -Check 'source and target are different users' -Passed $distinct -Detail $(if ($distinct) { "Moving from $sourceUpn to $targetUpn." } else { 'The source and target user are the same.' })),
            (New-Check -Check 'source user has the phone number assigned' -Passed $sourceHasNumber -Detail $(if ($sourceHasNumber) { "$sourceUpn currently holds $number." } else { "$sourceUpn does not hold the requested number." })),
            (New-Check -Check 'the number has a known tenant number type' -Passed $typeKnown -Detail $(if ($typeKnown) { "Number type is $numberType." } else { 'The number was not found in the tenant number inventory.' })),
            (New-Check -Check 'target is a user account' -Passed $isUserAccount -Detail $accountTypeDetail),
            (New-Check -Check 'target user is enterprise-voice capable' -Passed $voiceCapable -Detail $voiceCapableDetail),
            (New-Check -Check 'target user is licensed for the number type' -Passed $planLicensed -Detail $planDetail),
            (New-Check -Check 'target user has no phone number assigned' -Passed $targetFree -Detail $(if ($targetFree) { "$targetUpn has no assigned number." } else { "$targetUpn already has a number assigned; the move would overwrite it." }))
        )

        $failed = @($checks | Where-Object { -not $_.passed }).Count
        $summary = if ($failed -eq 0) {
            "All preflight checks passed for moving $number from $sourceUpn to $targetUpn."
        }
        else {
            "$failed preflight check(s) failed; the move was not attempted."
        }

        return (Write-StageResult -Summary $summary -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber'
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $source = Get-SnapshotValue -Snapshot $snapshot -Name 'source'
        $target = Get-SnapshotValue -Snapshot $snapshot -Name 'target'
        $targetEnabled = [bool](Get-PropertyValue -InputObject $target -Name 'enterpriseVoiceEnabled' -Default $false)

        $plannedCommands = @(
            "Remove-CsPhoneNumberAssignment -Identity '$sourceUpn' -PhoneNumber '$number' -PhoneNumberType $numberType",
            "Set-CsPhoneNumberAssignment -Identity '$targetUpn' -PhoneNumber '$number' -PhoneNumberType $numberType"
        )
        if (-not $targetEnabled) {
            $plannedCommands += "Set-CsPhoneNumberAssignment -Identity '$targetUpn' -EnterpriseVoiceEnabled `$true"
        }

        $after = [ordered]@{
            phoneNumber     = $number
            phoneNumberType = $numberType
            source          = [ordered]@{
                userPrincipalName      = [string](Get-PropertyValue -InputObject $source -Name 'userPrincipalName' -Default $sourceUpn)
                enterpriseVoiceEnabled = [bool](Get-PropertyValue -InputObject $source -Name 'enterpriseVoiceEnabled' -Default $false)
                lineUri                = $null
            }
            target          = [ordered]@{
                userPrincipalName      = [string](Get-PropertyValue -InputObject $target -Name 'userPrincipalName' -Default $targetUpn)
                enterpriseVoiceEnabled = $true
                lineUri                = $number
            }
            plannedCommands = $plannedCommands
        }

        return (Write-StageResult -Summary "Would move $number from $sourceUpn to $targetUpn." -After $after)
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        if ([string]::IsNullOrWhiteSpace($number)) {
            throw "No phone number could be resolved for the move; nothing was changed."
        }

        if ([string]::IsNullOrWhiteSpace($numberType)) {
            throw "The number type could not be resolved from the tenant inventory; nothing was changed."
        }

        $liveSource = Get-UserState -Upn $sourceUpn
        $liveTarget = Get-UserState -Upn $targetUpn

        if ($liveTarget.lineUri -eq $number) {
            # Idempotent: a retry of a call that already landed is not a failure.
            $after = [ordered]@{
                phoneNumber     = $number
                phoneNumberType = $numberType
                source          = $liveSource
                target          = $liveTarget
                changed         = $false
            }
            return (Write-StageResult -Summary "$number is already assigned to $targetUpn; no change was needed." -After $after)
        }

        if ($liveSource.lineUri -ne $number) {
            throw "The tenant state changed since the snapshot was captured: $sourceUpn no longer holds the number. Nothing was changed."
        }

        $null = Invoke-WithRetry -ScriptBlock {
            Remove-CsPhoneNumberAssignment -Identity $sourceUpn -PhoneNumber $number -PhoneNumberType $numberType -ErrorAction Stop
        }

        $null = Invoke-WithRetry -ScriptBlock {
            Set-CsPhoneNumberAssignment -Identity $targetUpn -PhoneNumber $number -PhoneNumberType $numberType -ErrorAction Stop
        }

        if (-not $liveTarget.enterpriseVoiceEnabled) {
            $null = Invoke-WithRetry -ScriptBlock {
                Set-CsPhoneNumberAssignment -Identity $targetUpn -EnterpriseVoiceEnabled $true -ErrorAction Stop
            }
        }

        $after = [ordered]@{
            phoneNumber     = $number
            phoneNumberType = $numberType
            source          = Get-UserState -Upn $sourceUpn
            target          = Get-UserState -Upn $targetUpn
            changed         = $true
        }

        return (Write-StageResult -Summary "Moved $number from $sourceUpn to $targetUpn." -After $after)
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')

        $assigned = Wait-ForCondition -TimeoutSeconds $script:VerifyTimeoutSeconds -PollIntervalSeconds $script:VerifyPollSeconds -Condition {
            (Get-UserState -Upn $targetUpn).lineUri -eq $number
        }

        $liveSource = Get-UserState -Upn $sourceUpn
        $liveTarget = Get-UserState -Upn $targetUpn
        $released = $liveSource.lineUri -ne $number

        $checks = @(
            (New-Check -Check 'number assigned to target' -Passed $assigned -Detail $(if ($assigned) { "$targetUpn holds $number." } else { "$targetUpn does not hold $number." })),
            (New-Check -Check 'number released from source' -Passed $released -Detail $(if ($released) { "$sourceUpn no longer holds $number." } else { "$sourceUpn still holds $number." })),
            (New-Check -Check 'target user is enterprise voice enabled' -Passed $liveTarget.enterpriseVoiceEnabled -Detail $(if ($liveTarget.enterpriseVoiceEnabled) { "$targetUpn is enterprise voice enabled." } else { "$targetUpn is not enterprise voice enabled." }))
        )

        $failed = @($checks | Where-Object { -not $_.passed }).Count
        $summary = if ($failed -eq 0) {
            "Verified $number is assigned to $targetUpn and released from $sourceUpn."
        }
        else {
            "$failed verification check(s) failed after the move."
        }

        return (Write-StageResult -Summary $summary -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $source = Get-SnapshotValue -Snapshot $snapshot -Name 'source'
        $sourceHadNumber = ([string](Get-PropertyValue -InputObject $source -Name 'lineUri')) -eq $number
        $sourceWasEnabled = [bool](Get-PropertyValue -InputObject $source -Name 'enterpriseVoiceEnabled' -Default $false)

        if ([string]::IsNullOrWhiteSpace($number) -or [string]::IsNullOrWhiteSpace($numberType) -or -not $sourceHadNumber) {
            # Nothing was captured that can be restored; the change never landed.
            return (Write-StageResult -Summary 'No assignment change needed to be undone.' -After ([ordered]@{ restored = $false }))
        }

        $liveTarget = Get-UserState -Upn $targetUpn
        if ($liveTarget.lineUri -eq $number) {
            $null = Invoke-WithRetry -ScriptBlock {
                Remove-CsPhoneNumberAssignment -Identity $targetUpn -PhoneNumber $number -PhoneNumberType $numberType -ErrorAction Stop
            }
        }

        $liveSource = Get-UserState -Upn $sourceUpn
        if ($liveSource.lineUri -ne $number) {
            $null = Invoke-WithRetry -ScriptBlock {
                Set-CsPhoneNumberAssignment -Identity $sourceUpn -PhoneNumber $number -PhoneNumberType $numberType -ErrorAction Stop
            }
        }

        if ($sourceWasEnabled -and -not (Get-UserState -Upn $sourceUpn).enterpriseVoiceEnabled) {
            $null = Invoke-WithRetry -ScriptBlock {
                Set-CsPhoneNumberAssignment -Identity $sourceUpn -EnterpriseVoiceEnabled $true -ErrorAction Stop
            }
        }

        $after = [ordered]@{
            phoneNumber     = $number
            phoneNumberType = $numberType
            source          = Get-UserState -Upn $sourceUpn
            target          = Get-UserState -Upn $targetUpn
            restored        = $true
        }

        return (Write-StageResult -Summary "Rolled back: $number was returned to $sourceUpn." -After $after)
    }

    default {
        throw "Tool 'move-number-between-users' does not implement stage '$Stage'."
    }
}
