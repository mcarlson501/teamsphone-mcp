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
$script:PhoneSystemFeatures = @('phonesystem', 'mcovoiceconf', 'mcoev')
$script:CallingPlanFeatures = @('callingplan', 'domesticcalling', 'internationalcalling', 'domesticandinternationalcalling', 'mcopstn')

function Test-FeatureMatch {
    param([string[]]$FeatureTypes, [string[]]$Patterns)

    foreach ($feature in $FeatureTypes) {
        $normalized = ($feature -replace '[\s_\-]', '').ToLowerInvariant()
        foreach ($pattern in $Patterns) {
            if ($normalized -like "$pattern*") { return $true }
        }
    }

    return $false
}

function Get-UserState {
    param([Parameter(Mandatory = $true)][string]$Upn)

    $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    if ($null -eq $user) { throw "User '$Upn' was not found in the tenant." }

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
        tenantDialPlan           = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'TenantDialPlan')
        teamsCallingPolicy       = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'TeamsCallingPolicy')
    }
}

function Get-NumberAssignment {
    param([Parameter(Mandatory = $true)][string]$Number)

    $assignments = @(Invoke-WithRetry -ScriptBlock {
        Get-CsPhoneNumberAssignment -TelephoneNumber $Number -ErrorAction Stop
    } | Where-Object { $null -ne $_ })

    if ($assignments.Count -eq 0) { return $null }
    return $assignments[0]
}

function Get-LocationState {
    param([AllowNull()][string]$Id)

    if ([string]::IsNullOrWhiteSpace($Id)) {
        return [ordered]@{ locationId = $null; exists = $false; validated = $false }
    }
    if (-not (Test-IsGuid -Value $Id)) {
        return [ordered]@{ locationId = $Id; exists = $false; validated = $false }
    }

    $locations = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineLisLocation -LocationId ([Guid]$Id) -ErrorAction Stop } | Where-Object { $null -ne $_ })
    if ($locations.Count -eq 0) {
        return [ordered]@{ locationId = $Id; exists = $false; validated = $false }
    }
    return [ordered]@{
        locationId = $Id
        exists     = $true
        validated  = Test-TeamsEmergencyLocationValidated -Location $locations[0]
    }
}

function Test-PolicyExists {
    param([AllowNull()][string]$Name, [object[]]$Policies)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $true }

    foreach ($policy in $Policies) {
        $identity = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $policy -Name 'Identity')
        $normalizedIdentity = if ($identity -like 'Tag:*') { $identity.Substring(4) } else { $identity }
        if ([string]::Equals($normalizedIdentity, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-RequestedPolicies {
    param([Parameter(Mandatory = $true)][object]$InputObject)

    return [ordered]@{
        onlineVoiceRoutingPolicy = [string](Get-PropertyValue -InputObject $InputObject -Name 'onlineVoiceRoutingPolicy')
        tenantDialPlan           = [string](Get-PropertyValue -InputObject $InputObject -Name 'tenantDialPlan')
        teamsCallingPolicy       = [string](Get-PropertyValue -InputObject $InputObject -Name 'teamsCallingPolicy')
    }
}

function Get-PolicyAvailability {
    param([Parameter(Mandatory = $true)][object]$RequestedPolicies)

    $voiceRouting = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name 'onlineVoiceRoutingPolicy')
    $dialPlan = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name 'tenantDialPlan')
    $calling = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name 'teamsCallingPolicy')

    return [ordered]@{
        onlineVoiceRoutingPolicy = if ([string]::IsNullOrWhiteSpace($voiceRouting)) {
            $true
        } else {
            Test-PolicyExists -Name $voiceRouting -Policies @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineVoiceRoutingPolicy -ErrorAction Stop })
        }
        tenantDialPlan = if ([string]::IsNullOrWhiteSpace($dialPlan)) {
            $true
        } else {
            Test-PolicyExists -Name $dialPlan -Policies @(Invoke-WithRetry -ScriptBlock { Get-CsTenantDialPlan -ErrorAction Stop })
        }
        teamsCallingPolicy = if ([string]::IsNullOrWhiteSpace($calling)) {
            $true
        } else {
            Test-PolicyExists -Name $calling -Policies @(Invoke-WithRetry -ScriptBlock { Get-CsTeamsCallingPolicy -ErrorAction Stop })
        }
    }
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

function Test-PolicyAssignmentsMatch {
    param([Parameter(Mandatory = $true)][object]$User, [Parameter(Mandatory = $true)][object]$RequestedPolicies)

    foreach ($name in @('onlineVoiceRoutingPolicy', 'tenantDialPlan', 'teamsCallingPolicy')) {
        $requested = [string](Get-PropertyValue -InputObject $RequestedPolicies -Name $name)
        if ([string]::IsNullOrWhiteSpace($requested)) { continue }

        $assigned = [string](Get-PropertyValue -InputObject $User -Name $name)
        if (-not [string]::Equals($assigned, $requested, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Restore-UserState {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$Upn
    )

    $original = Get-SnapshotValue -Snapshot $Snapshot -Name 'user'
    $number = [string](Get-SnapshotValue -Snapshot $Snapshot -Name 'phoneNumber')
    $numberType = [string](Get-SnapshotValue -Snapshot $Snapshot -Name 'phoneNumberType')
    $live = Get-UserState -Upn $Upn

    $originalLine = [string](Get-PropertyValue -InputObject $original -Name 'lineUri')
    if ($live.lineUri -eq $number -and $originalLine -ne $number) {
        $null = Invoke-WithRetry -ScriptBlock {
            Remove-CsPhoneNumberAssignment -Identity $Upn -PhoneNumber $number -PhoneNumberType $numberType -ErrorAction Stop
        }
        $originalLocationId = [string](Get-SnapshotValue -Snapshot $Snapshot -Name 'numberLocationId')
        if (-not [string]::IsNullOrWhiteSpace($originalLocationId)) {
            $null = Invoke-WithRetry -ScriptBlock {
                Set-CsPhoneNumberAssignment -PhoneNumber $number -LocationId $originalLocationId -ErrorAction Stop
            }
        }
        $live = Get-UserState -Upn $Upn
    }

    $originalEv = [bool](Get-PropertyValue -InputObject $original -Name 'enterpriseVoiceEnabled' -Default $false)
    if ($live.enterpriseVoiceEnabled -ne $originalEv) {
        $null = Invoke-WithRetry -ScriptBlock {
            Set-CsPhoneNumberAssignment -Identity $Upn -EnterpriseVoiceEnabled $originalEv -ErrorAction Stop
        }
    }

    $originalVoiceRouting = Get-PropertyValue -InputObject $original -Name 'onlineVoiceRoutingPolicy'
    $originalDialPlan = Get-PropertyValue -InputObject $original -Name 'tenantDialPlan'
    $originalCalling = Get-PropertyValue -InputObject $original -Name 'teamsCallingPolicy'

    if (-not [string]::Equals([string]$live.onlineVoiceRoutingPolicy, [string]$originalVoiceRouting, [System.StringComparison]::OrdinalIgnoreCase)) {
        $null = Invoke-WithRetry -ScriptBlock { Grant-CsOnlineVoiceRoutingPolicy -Identity $Upn -PolicyName $originalVoiceRouting -ErrorAction Stop }
    }
    if (-not [string]::Equals([string]$live.tenantDialPlan, [string]$originalDialPlan, [System.StringComparison]::OrdinalIgnoreCase)) {
        $null = Invoke-WithRetry -ScriptBlock { Grant-CsTenantDialPlan -Identity $Upn -PolicyName $originalDialPlan -ErrorAction Stop }
    }
    if (-not [string]::Equals([string]$live.teamsCallingPolicy, [string]$originalCalling, [System.StringComparison]::OrdinalIgnoreCase)) {
        $null = Invoke-WithRetry -ScriptBlock { Grant-CsTeamsCallingPolicy -Identity $Upn -PolicyName $originalCalling -ErrorAction Stop }
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$snapshot = Get-PropertyValue -InputObject $payload -Name 'snapshot'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')
$requestedNumberRaw = [string](Get-PropertyValue -InputObject $toolInput -Name 'phoneNumber')
$requestedLocationId = [string](Get-PropertyValue -InputObject $toolInput -Name 'locationId')
$requestedPolicies = Get-RequestedPolicies -InputObject $toolInput

switch ($Stage) {
    'snapshot' {
        $number = ConvertTo-E164Number -Value $requestedNumberRaw
        if ($null -eq $number) { throw 'The supplied phoneNumber is not a valid E.164 number.' }

        $user = Get-UserState -Upn $userUpn
        $assignment = Get-NumberAssignment -Number $number
        $availability = Get-PolicyAvailability -RequestedPolicies $requestedPolicies

        $state = [ordered]@{
            phoneNumber        = $number
            phoneNumberType    = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'NumberType') } else { $null }
            assignmentStatus   = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'PstnAssignmentStatus') } else { $null }
            assignedPstnTarget = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'AssignedPstnTargetId') } else { $null }
            numberLocationId   = if ($null -ne $assignment) { [string](Get-PropertyValue -InputObject $assignment -Name 'LocationId') } else { $null }
            requestedLocation  = Get-LocationState -Id $requestedLocationId
            user               = $user
            requestedPolicies  = $requestedPolicies
            policyAvailability = $availability
            capturedAt         = (Get-Date).ToUniversalTime().ToString('o')
        }

        return (Write-StageSnapshot -State $state)
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $assignmentStatus = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'assignmentStatus')
        $assignedTarget = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'assignedPstnTarget')
        $user = Get-SnapshotValue -Snapshot $snapshot -Name 'user'
        $availability = Get-SnapshotValue -Snapshot $snapshot -Name 'policyAvailability'

        $accountType = [string](Get-PropertyValue -InputObject $user -Name 'accountType')
        $accountTypeKnown = -not [string]::IsNullOrWhiteSpace($accountType)
        $isUserAccount = (-not $accountTypeKnown) -or [string]::Equals($accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)
        $targetLine = [string](Get-PropertyValue -InputObject $user -Name 'lineUri')
        $targetAlreadyAssigned = [string]::Equals($targetLine, $number, [System.StringComparison]::OrdinalIgnoreCase)
        $targetFree = [string]::IsNullOrWhiteSpace($targetLine) -or $targetAlreadyAssigned
        $typeKnown = -not [string]::IsNullOrWhiteSpace($numberType)
        $assignmentTargetsUser = -not [string]::IsNullOrWhiteSpace($assignedTarget) -and (
            [string]::Equals($assignedTarget, [string](Get-PropertyValue -InputObject $user -Name 'objectId'), [System.StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($assignedTarget, [string](Get-PropertyValue -InputObject $user -Name 'userPrincipalName'), [System.StringComparison]::OrdinalIgnoreCase)
        )
        $numberFree = (
            [string]::Equals($assignmentStatus, 'Unassigned', [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::IsNullOrWhiteSpace($assignedTarget)
        ) -or ($targetAlreadyAssigned -and $assignmentTargetsUser)

        $features = @(Get-PropertyValue -InputObject $user -Name 'featureTypes' -Default @())
        $licenseReported = $features.Count -gt 0
        $phoneSystem = Test-FeatureMatch -FeatureTypes $features -Patterns $script:PhoneSystemFeatures
        $voiceCapable = $phoneSystem -or (-not $licenseReported)
        $callingPlanRequired = [string]::Equals($numberType, 'CallingPlan', [System.StringComparison]::OrdinalIgnoreCase)
        $callingPlan = Test-FeatureMatch -FeatureTypes $features -Patterns $script:CallingPlanFeatures
        $numberLicensed = (-not $callingPlanRequired) -or (-not $licenseReported) -or $callingPlan

        $missingPolicies = @()
        foreach ($name in @('onlineVoiceRoutingPolicy', 'tenantDialPlan', 'teamsCallingPolicy')) {
            if (-not [bool](Get-PropertyValue -InputObject $availability -Name $name -Default $false)) {
                $missingPolicies += [string](Get-PropertyValue -InputObject $requestedPolicies -Name $name)
            }
        }
        $policiesExist = $missingPolicies.Count -eq 0
        $existingLocationId = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'numberLocationId')
        $requestedLocation = Get-SnapshotValue -Snapshot $snapshot -Name 'requestedLocation'
        $requestedLocationReady = [bool](Get-PropertyValue -InputObject $requestedLocation -Name 'exists' -Default $false) -and
            [bool](Get-PropertyValue -InputObject $requestedLocation -Name 'validated' -Default $false)
        $locationReady = (-not $callingPlanRequired) -or
            (-not [string]::IsNullOrWhiteSpace($existingLocationId)) -or
            $requestedLocationReady

        $checks = @(
            (New-Check -Check 'target is a user account' -Passed $isUserAccount -Detail $(if ($isUserAccount) { "$userUpn is a user account." } else { "$userUpn is a $accountType, which cannot hold a user phone number." })),
            (New-Check -Check 'target user has no phone number assigned' -Passed $targetFree -Detail $(if ($targetAlreadyAssigned) { "$userUpn already has the requested number." } elseif ($targetFree) { "$userUpn has no assigned number." } else { "$userUpn already has a different number assigned." })),
            (New-Check -Check 'the number has a known tenant number type' -Passed $typeKnown -Detail $(if ($typeKnown) { "Number type is $numberType." } else { "$number was not found in tenant inventory." })),
            (New-Check -Check 'the number is unassigned' -Passed $numberFree -Detail $(if ($targetAlreadyAssigned -and $assignmentTargetsUser) { "$number is already assigned to $userUpn." } elseif ($numberFree) { "$number is unassigned." } else { "$number is not available for assignment." })),
            (New-Check -Check 'target user is enterprise-voice capable' -Passed $voiceCapable -Detail $(if ($voiceCapable) { "$userUpn is enterprise-voice capable." } else { "$userUpn has no Phone System license reported by the tenant." })),
            (New-Check -Check 'target user is licensed for the number type' -Passed $numberLicensed -Detail $(if ($numberLicensed) { "$userUpn is licensed for $numberType." } else { "$userUpn has no Calling Plan license for a $numberType number." })),
            (New-Check -Check 'requested policies exist' -Passed $policiesExist -Detail $(if ($policiesExist) { 'All requested policies exist.' } else { "Missing policies: $($missingPolicies -join ', ')." })),
            (New-Check -Check 'Calling Plan number has a validated emergency location' -Passed $locationReady -Detail $(if ($locationReady) { 'The number has, or will receive, a validated emergency location.' } else { 'A validated locationId is required before this Calling Plan number can be assigned.' }))
        )

        $failed = @($checks | Where-Object { -not $_.passed }).Count
        $summary = if ($failed -eq 0) {
            "All preflight checks passed for assigning $number to $userUpn."
        } else {
            "$failed preflight check(s) failed; the assignment was not attempted."
        }

        return (Write-StageResult -Summary $summary -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $user = Get-SnapshotValue -Snapshot $snapshot -Name 'user'
        $plannedCommands = @()

        if (-not [bool](Get-PropertyValue -InputObject $user -Name 'enterpriseVoiceEnabled' -Default $false)) {
            $plannedCommands += "Set-CsPhoneNumberAssignment -Identity '$userUpn' -EnterpriseVoiceEnabled `$true"
        }
        $locationArgument = if ([string]::IsNullOrWhiteSpace($requestedLocationId)) { '' } else { " -LocationId '$requestedLocationId'" }
        $plannedCommands += "Set-CsPhoneNumberAssignment -Identity '$userUpn' -PhoneNumber '$number' -PhoneNumberType $numberType$locationArgument"

        $voiceRouting = [string](Get-PropertyValue -InputObject $requestedPolicies -Name 'onlineVoiceRoutingPolicy')
        $dialPlan = [string](Get-PropertyValue -InputObject $requestedPolicies -Name 'tenantDialPlan')
        $calling = [string](Get-PropertyValue -InputObject $requestedPolicies -Name 'teamsCallingPolicy')
        if (-not [string]::IsNullOrWhiteSpace($voiceRouting)) { $plannedCommands += "Grant-CsOnlineVoiceRoutingPolicy -Identity '$userUpn' -PolicyName '$voiceRouting'" }
        if (-not [string]::IsNullOrWhiteSpace($dialPlan)) { $plannedCommands += "Grant-CsTenantDialPlan -Identity '$userUpn' -PolicyName '$dialPlan'" }
        if (-not [string]::IsNullOrWhiteSpace($calling)) { $plannedCommands += "Grant-CsTeamsCallingPolicy -Identity '$userUpn' -PolicyName '$calling'" }

        $after = [ordered]@{
            userPrincipalName        = $userUpn
            enterpriseVoiceEnabled   = $true
            lineUri                  = $number
            phoneNumberType          = $numberType
            onlineVoiceRoutingPolicy = if ([string]::IsNullOrWhiteSpace($voiceRouting)) { Get-PropertyValue -InputObject $user -Name 'onlineVoiceRoutingPolicy' } else { $voiceRouting }
            tenantDialPlan           = if ([string]::IsNullOrWhiteSpace($dialPlan)) { Get-PropertyValue -InputObject $user -Name 'tenantDialPlan' } else { $dialPlan }
            teamsCallingPolicy       = if ([string]::IsNullOrWhiteSpace($calling)) { Get-PropertyValue -InputObject $user -Name 'teamsCallingPolicy' } else { $calling }
            plannedCommands          = $plannedCommands
        }

        return (Write-StageResult -Summary "Would assign $number to $userUpn." -After $after)
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $numberType = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumberType')
        $availability = Get-SnapshotValue -Snapshot $snapshot -Name 'policyAvailability'
        $live = Get-UserState -Upn $userUpn
        $liveAssignment = Get-NumberAssignment -Number $number

        if ([string]::IsNullOrWhiteSpace($numberType)) { throw 'The number type could not be resolved from tenant inventory; nothing was changed.' }
        if (-not [string]::IsNullOrWhiteSpace($live.accountType) -and -not [string]::Equals($live.accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$userUpn is not a user account; nothing was changed."
        }
        if (-not [string]::IsNullOrWhiteSpace($live.lineUri) -and $live.lineUri -ne $number) {
            throw "The tenant state changed since the snapshot: $userUpn now has a different number. Nothing was changed."
        }
        if ($live.lineUri -ne $number) {
            $status = [string](Get-PropertyValue -InputObject $liveAssignment -Name 'PstnAssignmentStatus')
            $assignedTarget = [string](Get-PropertyValue -InputObject $liveAssignment -Name 'AssignedPstnTargetId')
            if (-not [string]::Equals($status, 'Unassigned', [System.StringComparison]::OrdinalIgnoreCase) -or -not [string]::IsNullOrWhiteSpace($assignedTarget)) {
                throw "The tenant state changed since the snapshot: $number is no longer unassigned. Nothing was changed."
            }
        }

        $features = @($live.featureTypes)
        if ($features.Count -gt 0 -and -not (Test-FeatureMatch -FeatureTypes $features -Patterns $script:PhoneSystemFeatures)) {
            throw "$userUpn no longer has a Phone System license; nothing was changed."
        }
        if ([string]::Equals($numberType, 'CallingPlan', [System.StringComparison]::OrdinalIgnoreCase) -and
            $features.Count -gt 0 -and
            -not (Test-FeatureMatch -FeatureTypes $features -Patterns $script:CallingPlanFeatures)) {
            throw "$userUpn no longer has a Calling Plan license; nothing was changed."
        }
        foreach ($name in @('onlineVoiceRoutingPolicy', 'tenantDialPlan', 'teamsCallingPolicy')) {
            if (-not [bool](Get-PropertyValue -InputObject $availability -Name $name -Default $false)) {
                throw 'A requested policy no longer exists; nothing was changed.'
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($requestedLocationId)) {
            $liveLocation = Get-LocationState -Id $requestedLocationId
            if (-not $liveLocation.exists -or -not $liveLocation.validated) {
                throw 'The requested emergency location no longer exists as a validated location; nothing was changed.'
            }
        }

        $changed = $false
        try {
            if (-not $live.enterpriseVoiceEnabled) {
                $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -Identity $userUpn -EnterpriseVoiceEnabled $true -ErrorAction Stop }
                $changed = $true
            }
            if ($live.lineUri -ne $number) {
                $assignmentParameters = @{
                    Identity        = $userUpn
                    PhoneNumber     = $number
                    PhoneNumberType = $numberType
                    ErrorAction     = 'Stop'
                }
                if (-not [string]::IsNullOrWhiteSpace($requestedLocationId)) {
                    $assignmentParameters.LocationId = $requestedLocationId
                }
                $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment @assignmentParameters }
                $changed = $true
            }

            $voiceRouting = [string](Get-PropertyValue -InputObject $requestedPolicies -Name 'onlineVoiceRoutingPolicy')
            $dialPlan = [string](Get-PropertyValue -InputObject $requestedPolicies -Name 'tenantDialPlan')
            $calling = [string](Get-PropertyValue -InputObject $requestedPolicies -Name 'teamsCallingPolicy')
            if (-not [string]::IsNullOrWhiteSpace($voiceRouting) -and $live.onlineVoiceRoutingPolicy -ne $voiceRouting) {
                $null = Invoke-WithRetry -ScriptBlock { Grant-CsOnlineVoiceRoutingPolicy -Identity $userUpn -PolicyName $voiceRouting -ErrorAction Stop }
                $changed = $true
            }
            if (-not [string]::IsNullOrWhiteSpace($dialPlan) -and $live.tenantDialPlan -ne $dialPlan) {
                $null = Invoke-WithRetry -ScriptBlock { Grant-CsTenantDialPlan -Identity $userUpn -PolicyName $dialPlan -ErrorAction Stop }
                $changed = $true
            }
            if (-not [string]::IsNullOrWhiteSpace($calling) -and $live.teamsCallingPolicy -ne $calling) {
                $null = Invoke-WithRetry -ScriptBlock { Grant-CsTeamsCallingPolicy -Identity $userUpn -PolicyName $calling -ErrorAction Stop }
                $changed = $true
            }
        }
        catch {
            $originalError = $_
            try { Restore-UserState -Snapshot $snapshot -Upn $userUpn }
            catch { throw 'The assignment failed and automatic restoration also failed; manual intervention is required.' }
            throw $originalError
        }

        return (Write-StageResult -Summary $(if ($changed) { "Assigned $number to $userUpn." } else { "$number is already assigned to $userUpn with the requested settings." }) -After ((Get-UserState -Upn $userUpn) + [ordered]@{ phoneNumberType = $numberType; changed = $changed }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage

        $number = [string](Get-SnapshotValue -Snapshot $snapshot -Name 'phoneNumber')
        $assigned = Wait-ForCondition -TimeoutSeconds $script:VerifyTimeoutSeconds -PollIntervalSeconds $script:VerifyPollSeconds -Condition {
            (Get-UserState -Upn $userUpn).lineUri -eq $number
        }
        $live = Get-UserState -Upn $userUpn
        $policiesAssigned = Test-PolicyAssignmentsMatch -User $live -RequestedPolicies $requestedPolicies

        $checks = @(
            (New-Check -Check 'number assigned to target' -Passed $assigned -Detail $(if ($assigned) { "$userUpn holds $number." } else { "$userUpn does not hold $number." })),
            (New-Check -Check 'target user is enterprise voice enabled' -Passed $live.enterpriseVoiceEnabled -Detail $(if ($live.enterpriseVoiceEnabled) { "$userUpn is enterprise voice enabled." } else { "$userUpn is not enterprise voice enabled." })),
            (New-Check -Check 'requested policies assigned' -Passed $policiesAssigned -Detail $(if ($policiesAssigned) { 'All requested policies are assigned.' } else { 'One or more requested policies are not assigned.' }))
        )

        $failed = @($checks | Where-Object { -not $_.passed }).Count
        $summary = if ($failed -eq 0) { "Verified $number is assigned to $userUpn." } else { "$failed verification check(s) failed after the assignment." }
        return (Write-StageResult -Summary $summary -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        Restore-UserState -Snapshot $snapshot -Upn $userUpn
        return (Write-StageResult -Summary "Restored the pre-assignment state for $userUpn." -After (Get-UserState -Upn $userUpn))
    }

    default {
        throw "Tool 'assign-phone-number' does not implement stage '$Stage'."
    }
}