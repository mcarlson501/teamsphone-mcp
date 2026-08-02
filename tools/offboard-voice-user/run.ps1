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

$script:RemoveNumberScript = Join-Path $PSScriptRoot '..' 'remove-phone-number' 'run.ps1'
$script:PolicyNames = @('onlineVoiceRoutingPolicy', 'tenantDialPlan', 'teamsCallingPolicy', 'callerIdPolicy')

function Get-UserState {
    param([Parameter(Mandatory = $true)][string]$Upn)
    $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $Upn -ErrorAction Stop }
    if ($null -eq $user) { throw "User '$Upn' was not found in the tenant." }
    $objectId = Get-PropertyValue -InputObject $user -Name 'Identity'
    if ($null -eq $objectId) { $objectId = Get-PropertyValue -InputObject $user -Name 'ObjectId' }
    $callerId = Get-PropertyValue -InputObject $user -Name 'CallingLineIdentity'
    if ($null -eq $callerId) { $callerId = Get-PropertyValue -InputObject $user -Name 'CallingLineIdentityPolicy' }
    return [ordered]@{
        userPrincipalName        = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $Upn)
        objectId                 = [string]$objectId
        accountType              = [string](Get-PropertyValue -InputObject $user -Name 'AccountType')
        enterpriseVoiceEnabled   = [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)
        lineUri                  = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
        onlineVoiceRoutingPolicy = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'OnlineVoiceRoutingPolicy')
        tenantDialPlan           = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'TenantDialPlan')
        teamsCallingPolicy       = Get-AssignedPolicyName -Value (Get-PropertyValue -InputObject $user -Name 'TeamsCallingPolicy')
        callerIdPolicy           = Get-AssignedPolicyName -Value $callerId
    }
}

function Get-QueueState {
    param([Parameter(Mandatory = $true)][object]$Queue)
    $agentIds = @()
    foreach ($agent in @(Get-PropertyValue -InputObject $Queue -Name 'Agents' -Default @())) {
        $objectId = Get-PropertyValue -InputObject $agent -Name 'ObjectId'
        if ($null -ne $objectId) { $agentIds += [string]$objectId }
    }
    return [ordered]@{
        identity            = [string](Get-PropertyValue -InputObject $Queue -Name 'Identity')
        name                = [string](Get-PropertyValue -InputObject $Queue -Name 'Name')
        agentObjectIds      = @($agentIds | Sort-Object -Unique)
        distributionListIds = @(Get-PropertyValue -InputObject $Queue -Name 'DistributionLists' -Default @() | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    }
}

function Get-AllQueueStates {
    return @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -ErrorAction Stop } | ForEach-Object { Get-QueueState -Queue $_ })
}

function Get-UserQueueMemberships {
    param([Parameter(Mandatory = $true)][string]$ObjectId)
    return @(Get-AllQueueStates | Where-Object { @($_.agentObjectIds) -contains $ObjectId })
}

function Get-QueueByIdentity {
    param([Parameter(Mandatory = $true)][string]$Identity)
    $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -Identity $Identity -ErrorAction Stop })
    if ($queues.Count -ne 1) { throw "Call queue '$Identity' could not be resolved uniquely." }
    return Get-QueueState -Queue $queues[0]
}

function Test-SetEqual {
    param([object[]]$Left, [object[]]$Right)
    $leftValues = @($Left | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    $rightValues = @($Right | ForEach-Object { [string]$_ } | Sort-Object -Unique)
    if ($leftValues.Count -ne $rightValues.Count) { return $false }
    return @(Compare-Object -ReferenceObject $leftValues -DifferenceObject $rightValues).Count -eq 0
}

function Set-PolicyAssignment {
    param([string]$PolicyType, [string]$Upn, [AllowNull()][string]$PolicyName)
    switch ($PolicyType) {
        'onlineVoiceRoutingPolicy' { $null = Invoke-WithRetry -ScriptBlock { Grant-CsOnlineVoiceRoutingPolicy -Identity $Upn -PolicyName $PolicyName -ErrorAction Stop } }
        'tenantDialPlan' { $null = Invoke-WithRetry -ScriptBlock { Grant-CsTenantDialPlan -Identity $Upn -PolicyName $PolicyName -ErrorAction Stop } }
        'teamsCallingPolicy' { $null = Invoke-WithRetry -ScriptBlock { Grant-CsTeamsCallingPolicy -Identity $Upn -PolicyName $PolicyName -ErrorAction Stop } }
        'callerIdPolicy' { $null = Invoke-WithRetry -ScriptBlock { Grant-CsCallingLineIdentity -Identity $Upn -PolicyName $PolicyName -ErrorAction Stop } }
        default { throw "Unsupported policy type '$PolicyType'." }
    }
}

function Invoke-NumberStage {
    param([string]$ChildStage, [object]$InputObject, [AllowNull()][object]$ChildSnapshot)
    $json = @{ input = $InputObject; snapshot = $ChildSnapshot; pagination = $null } | ConvertTo-Json -Depth 32
    return (& $script:RemoveNumberScript -Stage $ChildStage -InputJson $json | ConvertFrom-Json -Depth 32)
}

function Test-ChecksPassed {
    param([Parameter(Mandatory = $true)][object]$Result)
    return @($Result.checks | Where-Object { -not [bool]$_.passed }).Count -eq 0
}

function New-Check {
    param([string]$Check, [bool]$Passed, [string]$Detail)
    return [ordered]@{ check = $Check; passed = $Passed; detail = $Detail }
}

function Assert-Snapshot {
    param([AllowNull()][object]$Snapshot, [string]$StageName)
    if ($null -eq $Snapshot) { throw "Stage '$StageName' requires the captured snapshot but none was supplied." }
}

function Restore-Queues {
    param([object[]]$Queues, [string]$ObjectId)
    foreach ($queue in $Queues) {
        $live = Get-QueueByIdentity -Identity ([string]$queue.identity)
        $expectedAfter = @($queue.agentObjectIds | Where-Object { $_ -ne $ObjectId })
        if ((Test-SetEqual -Left @($live.agentObjectIds) -Right @($queue.agentObjectIds))) { continue }
        if (-not (Test-SetEqual -Left @($live.agentObjectIds) -Right $expectedAfter)) {
            throw "Call queue '$($queue.name)' changed after offboarding; automatic restoration was not attempted."
        }
        $originalIds = @($queue.agentObjectIds)
        $null = Invoke-WithRetry -ScriptBlock { Set-CsCallQueue -Identity $queue.identity -Users $originalIds -ErrorAction Stop }
    }
}

function Restore-Policies {
    param([object]$Original, [string]$Upn)
    $live = Get-UserState -Upn $Upn
    foreach ($name in $script:PolicyNames) {
        $originalPolicy = Get-PropertyValue -InputObject $Original -Name $name
        $livePolicy = Get-PropertyValue -InputObject $live -Name $name
        if (-not [string]::Equals([string]$livePolicy, [string]$originalPolicy, [System.StringComparison]::OrdinalIgnoreCase)) {
            Set-PolicyAssignment -PolicyType $name -Upn $Upn -PolicyName $originalPolicy
        }
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$snapshot = Get-PropertyValue -InputObject $payload -Name 'snapshot'
$userUpn = [string](Get-PropertyValue -InputObject $toolInput -Name 'userUpn')
$numberInput = [ordered]@{ userUpn = $userUpn }

switch ($Stage) {
    'snapshot' {
        $user = Get-UserState -Upn $userUpn
        $numberSnapshot = if ([string]::IsNullOrWhiteSpace([string]$user.lineUri)) { $null } else { Invoke-NumberStage -ChildStage 'snapshot' -InputObject $numberInput -ChildSnapshot $null }
        return (Write-StageSnapshot -State ([ordered]@{
            user           = $user
            queueMemberships = @(Get-UserQueueMemberships -ObjectId ([string]$user.objectId))
            numberSnapshot = $numberSnapshot
            capturedAt     = (Get-Date).ToUniversalTime().ToString('o')
        }))
    }

    'preflight' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $accountType = [string](Get-PropertyValue -InputObject $user -Name 'accountType')
        $isUser = [string]::IsNullOrWhiteSpace($accountType) -or [string]::Equals($accountType, 'User', [System.StringComparison]::OrdinalIgnoreCase)
        $numberSnapshot = Get-PropertyValue -InputObject $snapshot -Name 'numberSnapshot'
        $numberReady = $true
        $numberDetail = 'The user has no phone number to release.'
        if ($null -ne $numberSnapshot) {
            $numberPreflight = Invoke-NumberStage -ChildStage 'preflight' -InputObject $numberInput -ChildSnapshot $numberSnapshot
            $numberReady = Test-ChecksPassed -Result $numberPreflight
            $numberDetail = [string]$numberPreflight.summary
        }
        $queueCount = @(Get-PropertyValue -InputObject $snapshot -Name 'queueMemberships' -Default @()).Count
        $checks = @(
            (New-Check -Check 'target is a user account' -Passed $isUser -Detail $(if ($isUser) { "$userUpn is a user account." } else { "$userUpn is a $accountType." })),
            (New-Check -Check 'direct call queue memberships captured' -Passed $true -Detail "$queueCount direct queue membership(s) captured."),
            (New-Check -Check 'assigned number can be released' -Passed $numberReady -Detail $numberDetail),
            (New-Check -Check 'voice assignments captured for rollback' -Passed $true -Detail 'Enterprise voice and policy assignments were captured.')
        )
        $failed = @($checks | Where-Object { -not $_.passed }).Count
        return (Write-StageResult -Summary $(if ($failed -eq 0) { 'All voice offboarding preflight checks passed.' } else { "$failed voice offboarding preflight step(s) failed." }) -Checks $checks)
    }

    'dryrun' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $user = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $queues = @(Get-PropertyValue -InputObject $snapshot -Name 'queueMemberships' -Default @())
        $commands = @($queues | ForEach-Object { "Set-CsCallQueue -Identity '$($_.identity)' -Users <members excluding $($user.objectId)>" })
        $numberSnapshot = Get-PropertyValue -InputObject $snapshot -Name 'numberSnapshot'
        if ($null -ne $numberSnapshot) {
            $numberPlan = Invoke-NumberStage -ChildStage 'dryrun' -InputObject $numberInput -ChildSnapshot $numberSnapshot
            $commands += @(Get-PropertyValue -InputObject $numberPlan.after -Name 'plannedCommands' -Default @())
        }
        foreach ($name in $script:PolicyNames) {
            if (-not [string]::IsNullOrWhiteSpace([string](Get-PropertyValue -InputObject $user -Name $name))) { $commands += "Clear $name assignment on '$userUpn'" }
        }
        if ([bool]$user.enterpriseVoiceEnabled) { $commands += "Set-CsPhoneNumberAssignment -Identity '$userUpn' -EnterpriseVoiceEnabled false" }
        return (Write-StageResult -Summary "Would run $($commands.Count) voice offboarding change(s) for $userUpn." -After ([ordered]@{ plannedCommands = $commands; queueNames = @($queues | ForEach-Object { [string]$_.name }); phoneNumber = $user.lineUri }))
    }

    'execute' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $queues = @(Get-PropertyValue -InputObject $snapshot -Name 'queueMemberships' -Default @())
        $completedQueues = [System.Collections.Generic.List[object]]::new()
        $numberRemoved = $false
        $clearedPolicies = [System.Collections.Generic.List[string]]::new()
        $enterpriseVoiceDisabled = $false
        try {
            foreach ($queue in $queues) {
                $live = Get-QueueByIdentity -Identity ([string]$queue.identity)
                if (-not (Test-SetEqual -Left @($live.agentObjectIds) -Right @($queue.agentObjectIds))) {
                    throw "Call queue '$($queue.name)' membership changed since the snapshot; nothing further was changed."
                }
                $remaining = @($live.agentObjectIds | Where-Object { $_ -ne $original.objectId })
                $null = Invoke-WithRetry -ScriptBlock { Set-CsCallQueue -Identity $live.identity -Users $remaining -ErrorAction Stop }
                $completedQueues.Add($queue)
            }

            $numberSnapshot = Get-PropertyValue -InputObject $snapshot -Name 'numberSnapshot'
            if ($null -ne $numberSnapshot) {
                $null = Invoke-NumberStage -ChildStage 'execute' -InputObject $numberInput -ChildSnapshot $numberSnapshot
                $numberRemoved = $true
            }

            $liveUser = Get-UserState -Upn $userUpn
            foreach ($name in $script:PolicyNames) {
                $current = [string](Get-PropertyValue -InputObject $liveUser -Name $name)
                if ([string]::IsNullOrWhiteSpace($current)) { continue }
                Set-PolicyAssignment -PolicyType $name -Upn $userUpn -PolicyName $null
                $clearedPolicies.Add($name)
            }
            if ($liveUser.enterpriseVoiceEnabled) {
                $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -Identity $userUpn -EnterpriseVoiceEnabled $false -ErrorAction Stop }
                $enterpriseVoiceDisabled = $true
            }
        } catch {
            $originalError = $_
            try {
                if ($enterpriseVoiceDisabled -and [bool]$original.enterpriseVoiceEnabled) {
                    $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -Identity $userUpn -EnterpriseVoiceEnabled $true -ErrorAction Stop }
                }
                if ($clearedPolicies.Count -gt 0) { Restore-Policies -Original $original -Upn $userUpn }
                if ($numberRemoved) { $null = Invoke-NumberStage -ChildStage 'rollback' -InputObject $numberInput -ChildSnapshot $snapshot.numberSnapshot }
                Restore-Queues -Queues @($completedQueues) -ObjectId ([string]$original.objectId)
            } catch { throw 'Voice offboarding failed and compensation also failed; manual intervention is required.' }
            throw $originalError
        }

        $disposition = [ordered]@{
            userPrincipalName        = $userUpn
            enterpriseVoiceDisabled = [bool]$original.enterpriseVoiceEnabled
            phoneNumber              = $original.lineUri
            numberDisposition        = if ($null -eq $snapshot.numberSnapshot) { 'notAssigned' } else { 'releasedToTenantInventory' }
            removedQueueNames        = @($queues | ForEach-Object { [string]$_.name })
            clearedPolicies          = @($clearedPolicies)
        }
        return (Write-StageResult -Summary "Offboarded $userUpn from Teams Phone." -After ([ordered]@{ disposition = $disposition }))
    }

    'verify' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $remainingQueues = @(Get-UserQueueMemberships -ObjectId ([string]$original.objectId))
        $numberReleased = $true
        if ($null -ne $snapshot.numberSnapshot) {
            $numberReleased = Test-ChecksPassed -Result (Invoke-NumberStage -ChildStage 'verify' -InputObject $numberInput -ChildSnapshot $snapshot.numberSnapshot)
        }
        $live = Get-UserState -Upn $userUpn
        $policiesCleared = @($script:PolicyNames | Where-Object { -not [string]::IsNullOrWhiteSpace([string](Get-PropertyValue -InputObject $live -Name $_)) }).Count -eq 0
        $checks = @(
            (New-Check -Check 'direct call queue memberships removed' -Passed ($remainingQueues.Count -eq 0) -Detail "$($remainingQueues.Count) direct queue membership(s) remain."),
            (New-Check -Check 'phone number released to tenant inventory' -Passed $numberReleased -Detail $(if ($numberReleased) { 'The assigned number is released or no number was assigned.' } else { 'The assigned number was not fully released.' })),
            (New-Check -Check 'voice policy assignments cleared' -Passed $policiesCleared -Detail $(if ($policiesCleared) { 'Voice policy assignments are cleared.' } else { 'One or more voice policy assignments remain.' })),
            (New-Check -Check 'enterprise voice disabled' -Passed (-not $live.enterpriseVoiceEnabled) -Detail $(if (-not $live.enterpriseVoiceEnabled) { 'Enterprise voice is disabled.' } else { 'Enterprise voice remains enabled.' }))
        )
        return (Write-StageResult -Summary 'Completed voice offboarding verification.' -Checks $checks)
    }

    'rollback' {
        Assert-Snapshot -Snapshot $snapshot -StageName $Stage
        $original = Get-PropertyValue -InputObject $snapshot -Name 'user'
        $live = Get-UserState -Upn $userUpn
        if ([bool]$original.enterpriseVoiceEnabled -and -not $live.enterpriseVoiceEnabled) {
            $null = Invoke-WithRetry -ScriptBlock { Set-CsPhoneNumberAssignment -Identity $userUpn -EnterpriseVoiceEnabled $true -ErrorAction Stop }
        }
        if ($null -ne $snapshot.numberSnapshot) { $null = Invoke-NumberStage -ChildStage 'rollback' -InputObject $numberInput -ChildSnapshot $snapshot.numberSnapshot }
        Restore-Policies -Original $original -Upn $userUpn
        Restore-Queues -Queues @(Get-PropertyValue -InputObject $snapshot -Name 'queueMemberships' -Default @()) -ObjectId ([string]$original.objectId)
        return (Write-StageResult -Summary "Restored the pre-offboarding Teams Phone state for $userUpn." -After ([ordered]@{ restored = $true }))
    }

    default { throw "Tool 'offboard-voice-user' does not implement stage '$Stage'." }
}