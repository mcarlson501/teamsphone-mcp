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

$script:PhoneSystemFeatures = @('phonesystem', 'mcovoiceconf', 'mcoev')

function Add-OrphanFinding {
    param(
        [System.Collections.ArrayList]$Findings,
        [string]$Category,
        [string]$Severity,
        [string]$Code,
        [string]$Identity,
        [string]$What,
        [string]$Why,
        [string]$Fix
    )

    [void]$Findings.Add([ordered]@{
        category = $Category
        severity = $Severity
        code = $Code
        identity = $Identity
        what = $What
        why = $Why
        fix = $Fix
    })
}

function Test-PhoneSystemLicense {
    param([string[]]$FeatureTypes)

    foreach ($feature in $FeatureTypes) {
        $normalized = ($feature -replace '[\s_\-]', '').ToLowerInvariant()
        foreach ($pattern in $script:PhoneSystemFeatures) {
            if ($normalized -like "$pattern*") { return $true }
        }
    }

    return $false
}

function Normalize-PolicyIdentity {
    param([AllowNull()][object]$Value)

    $name = [string](Get-AssignedPolicyName -Value $Value)
    if ($name.StartsWith('Tag:', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $name.Substring(4)
    }
    return $name
}

function Get-ApplicationTargets {
    param($Attendant)

    $targets = [System.Collections.ArrayList]::new()
    $operator = Get-PropertyValue -InputObject $Attendant -Name 'Operator'
    if ($null -ne $operator) { [void]$targets.Add($operator) }

    $flows = @()
    $defaultFlow = Get-PropertyValue -InputObject $Attendant -Name 'DefaultCallFlow'
    if ($null -ne $defaultFlow) { $flows += $defaultFlow }
    $flows += @(Get-PropertyValue -InputObject $Attendant -Name 'CallFlows' -Default @())

    foreach ($flow in $flows) {
        $menu = Get-PropertyValue -InputObject $flow -Name 'Menu'
        foreach ($option in @(Get-PropertyValue -InputObject $menu -Name 'MenuOptions' -Default @())) {
            $target = Get-PropertyValue -InputObject $option -Name 'CallTarget'
            if ($null -ne $target) { [void]$targets.Add($target) }
        }
    }

    return @($targets)
}

$payload = Get-StageInput -InputJson $InputJson

switch ($Stage) {
    'execute' {
        $findings = [System.Collections.ArrayList]::new()
        $resourceAccounts = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineApplicationInstance -ErrorAction Stop })
        $resourceAccountIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

        foreach ($account in $resourceAccounts) {
            $objectId = [string](Get-PropertyValue -InputObject $account -Name 'ObjectId')
            $displayName = [string](Get-PropertyValue -InputObject $account -Name 'DisplayName' -Default $objectId)
            if (-not [string]::IsNullOrWhiteSpace($objectId)) { [void]$resourceAccountIds.Add($objectId) }

            $number = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $account -Name 'PhoneNumber'))
            if ($null -eq $number) {
                Add-OrphanFinding -Findings $findings -Category 'resourceAccounts' -Severity 'warning' -Code 'resourceAccountWithoutNumber' -Identity $objectId `
                    -What "Resource account '$displayName' has no phone number." `
                    -Why 'An unnumbered resource account cannot receive direct PSTN calls.' `
                    -Fix 'Assign an appropriate service number or remove the unused resource account.'
            }

            try {
                $association = Invoke-WithRetry -ScriptBlock {
                    Get-CsOnlineApplicationInstanceAssociation -Identity $objectId -ErrorAction Stop
                }
            }
            catch {
                $association = $null
            }
            if ($null -eq $association) {
                Add-OrphanFinding -Findings $findings -Category 'resourceAccounts' -Severity 'warning' -Code 'unattachedResourceAccount' -Identity $objectId `
                    -What "Resource account '$displayName' is not attached to a call queue or auto attendant." `
                    -Why 'The account may be leftover from a deleted or incomplete call flow.' `
                    -Fix 'Attach the account to its intended application or remove it.'
            }

            try {
                $accountUser = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $objectId -ErrorAction Stop }
                $features = @(Get-PropertyValue -InputObject $accountUser -Name 'FeatureTypes' -Default @() | ForEach-Object { [string]$_ })
                if (-not (Test-PhoneSystemLicense -FeatureTypes $features)) {
                    Add-OrphanFinding -Findings $findings -Category 'resourceAccounts' -Severity 'critical' -Code 'unlicensedResourceAccount' -Identity $objectId `
                        -What "Resource account '$displayName' has no Teams Phone resource-account entitlement reported." `
                        -Why 'A numbered or application-attached resource account may not function without the required entitlement.' `
                        -Fix 'Assign the appropriate Teams Phone Resource Account license in Microsoft 365.'
                }
            }
            catch {
                Add-OrphanFinding -Findings $findings -Category 'resourceAccounts' -Severity 'warning' -Code 'resourceAccountUserUnresolved' -Identity $objectId `
                    -What "Resource account '$displayName' could not be resolved as an online user." `
                    -Why 'The account may be deleted or inaccessible while still present in application inventory.' `
                    -Fix 'Verify the resource account in Teams admin center and remove stale application references.'
            }
        }

        $attendants = @(Invoke-WithRetry -ScriptBlock { Get-CsAutoAttendant -ErrorAction Stop })
        foreach ($attendant in $attendants) {
            $attendantName = [string](Get-PropertyValue -InputObject $attendant -Name 'Name')
            foreach ($target in @(Get-ApplicationTargets -Attendant $attendant)) {
                $targetType = [string](Get-PropertyValue -InputObject $target -Name 'Type')
                $targetId = [string](Get-PropertyValue -InputObject $target -Name 'Id')
                if ($targetType -match '^(?i)ApplicationEndpoint$' -and -not $resourceAccountIds.Contains($targetId)) {
                    Add-OrphanFinding -Findings $findings -Category 'autoAttendants' -Severity 'critical' -Code 'deletedApplicationTarget' -Identity $targetId `
                        -What "Auto attendant '$attendantName' targets missing application endpoint '$targetId'." `
                        -Why 'Calls selecting this route cannot reach the deleted call queue or auto attendant.' `
                        -Fix "Update '$attendantName' to target an existing resource account or remove the broken menu route."
                }
            }
        }

        $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -ErrorAction Stop })
        foreach ($queue in $queues) {
            $agents = @(Get-PropertyValue -InputObject $queue -Name 'Agents' -Default @())
            if ($agents.Count -eq 0) {
                $queueName = [string](Get-PropertyValue -InputObject $queue -Name 'Name')
                $queueId = [string](Get-PropertyValue -InputObject $queue -Name 'Identity')
                Add-OrphanFinding -Findings $findings -Category 'callQueues' -Severity 'critical' -Code 'emptyCallQueue' -Identity $queueId `
                    -What "Call queue '$queueName' has no configured agents." `
                    -Why 'Calls entering the queue cannot reach a person.' `
                    -Fix "Use update-callqueue-members to add agents to '$queueName' or remove the unused queue."
            }
        }

        $users = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -ResultSize ([int]::MaxValue) -ErrorAction Stop })
        $assignedVoicePolicies = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $assignedDialPlans = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($user in $users) {
            $upn = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName')
            $enterpriseVoiceEnabled = [bool](Get-PropertyValue -InputObject $user -Name 'EnterpriseVoiceEnabled' -Default $false)
            $lineUri = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $user -Name 'LineUri'))
            if ($enterpriseVoiceEnabled -and $null -eq $lineUri) {
                Add-OrphanFinding -Findings $findings -Category 'users' -Severity 'warning' -Code 'voiceEnabledUserWithoutNumber' -Identity $upn `
                    -What "Enterprise voice is enabled for '$upn', but no phone number is assigned." `
                    -Why 'The user consumes voice configuration without a complete PSTN identity.' `
                    -Fix 'Assign a number with assign-phone-number or disable enterprise voice if the user no longer needs Teams Phone.'
            }

            $voicePolicy = Normalize-PolicyIdentity -Value (Get-PropertyValue -InputObject $user -Name 'OnlineVoiceRoutingPolicy')
            $dialPlan = Normalize-PolicyIdentity -Value (Get-PropertyValue -InputObject $user -Name 'TenantDialPlan')
            if (-not [string]::IsNullOrWhiteSpace($voicePolicy)) { [void]$assignedVoicePolicies.Add($voicePolicy) }
            if (-not [string]::IsNullOrWhiteSpace($dialPlan)) { [void]$assignedDialPlans.Add($dialPlan) }
        }

        foreach ($policySpec in @(
            [ordered]@{ Category = 'voiceRoutingPolicies'; Policies = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineVoiceRoutingPolicy -ErrorAction Stop }); Assigned = $assignedVoicePolicies },
            [ordered]@{ Category = 'tenantDialPlans'; Policies = @(Invoke-WithRetry -ScriptBlock { Get-CsTenantDialPlan -ErrorAction Stop }); Assigned = $assignedDialPlans }
        )) {
            foreach ($policy in $policySpec.Policies) {
                $identity = Normalize-PolicyIdentity -Value (Get-PropertyValue -InputObject $policy -Name 'Identity')
                if ([string]::IsNullOrWhiteSpace($identity) -or $identity -eq 'Global') { continue }
                if (-not $policySpec.Assigned.Contains($identity)) {
                    Add-OrphanFinding -Findings $findings -Category 'policies' -Severity 'info' -Code 'unassignedCustomPolicy' -Identity "$($policySpec.Category):$identity" `
                        -What "Custom $($policySpec.Category) policy '$identity' is not assigned to a user." `
                        -Why 'Unused custom policies increase configuration drift and make intended policy state harder to understand.' `
                        -Fix 'Assign the policy to an intended user or remove it after confirming no group assignment depends on it.'
                }
            }
        }

        $severityRank = @{ critical = 0; warning = 1; info = 2 }
        $orderedFindings = @($findings | Sort-Object -Property @(
            @{ Expression = { $severityRank[$_.severity] } },
            @{ Expression = { $_.category } },
            @{ Expression = { $_.identity } }
        ))
        $counts = [ordered]@{
            critical = @($orderedFindings | Where-Object { $_.severity -eq 'critical' }).Count
            warning = @($orderedFindings | Where-Object { $_.severity -eq 'warning' }).Count
            info = @($orderedFindings | Where-Object { $_.severity -eq 'info' }).Count
            total = $orderedFindings.Count
        }
        $after = [ordered]@{
            status = if ($counts.critical -gt 0) { 'criticalIssues' } elseif ($counts.warning -gt 0) { 'issuesFound' } elseif ($counts.info -gt 0) { 'advisories' } else { 'healthy' }
            findingCounts = $counts
            findings = $orderedFindings
            inventoryCounts = [ordered]@{
                resourceAccounts = $resourceAccounts.Count
                autoAttendants = $attendants.Count
                callQueues = $queues.Count
                users = $users.Count
            }
        }

        return (Write-StageResult -Summary "Orphan discovery found $($counts.total) issue(s): $($counts.critical) critical, $($counts.warning) warning, and $($counts.info) informational." -After $after)
    }
    default {
        throw "Tool 'find-orphaned-objects' does not implement stage '$Stage'."
    }
}