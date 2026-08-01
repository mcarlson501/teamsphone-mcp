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

$script:Warnings = @()

function Add-SnapshotWarning {
    param(
        [string]$Section,
        [string]$Message
    )

    $script:Warnings += [ordered]@{
        section = $Section
        message = $Message
    }
}

function Invoke-SiblingTool {
    <#
        Reuses the sibling read tools instead of duplicating their filtering and
        shaping logic. Passing a null pagination block makes Select-StagePage
        return the full result set, which is what an aggregate snapshot needs.
    #>
    param(
        [string]$ToolId,
        [hashtable]$ToolInput
    )

    $scriptPath = Join-Path $PSScriptRoot '..' $ToolId 'run.ps1'
    $payload = @{ input = $ToolInput; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 6
    $output = & $scriptPath -Stage execute -InputJson $payload
    return ($output | ConvertFrom-Json).after
}

function Get-SnapshotSection {
    # A single failing area degrades to a warning so the rest of the snapshot
    # still reaches the caller.
    param(
        [string]$Section,
        [scriptblock]$ScriptBlock
    )

    try {
        return (& $ScriptBlock)
    }
    catch {
        Add-SnapshotWarning -Section $Section -Message $_.Exception.Message
        return $null
    }
}

function Group-CountBy {
    param(
        $Items,
        [string]$Property
    )

    $counts = [ordered]@{}
    foreach ($item in @($Items)) {
        $key = [string](Get-PropertyValue -InputObject $item -Name $Property)
        if ([string]::IsNullOrWhiteSpace($key)) { $key = 'unknown' }
        if (-not $counts.Contains($key)) { $counts[$key] = 0 }
        $counts[$key] = $counts[$key] + 1
    }

    return $counts
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$passthrough = @{
    tenantId      = [string](Get-PropertyValue -InputObject $toolInput -Name 'tenantId')
    credentialRef = [string](Get-PropertyValue -InputObject $toolInput -Name 'credentialRef')
}

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        $phoneNumbers = Get-SnapshotSection -Section 'phoneNumbers' -ScriptBlock {
            $data = Invoke-SiblingTool -ToolId 'list-phone-numbers' -ToolInput $passthrough
            [ordered]@{
                total      = $data.totalMatched
                assigned   = $data.assignedCount
                unassigned = $data.unassignedCount
                byType     = Group-CountBy -Items $data.numbers -Property 'numberType'
            }
        }

        $resourceAccounts = Get-SnapshotSection -Section 'resourceAccounts' -ScriptBlock {
            $data = Invoke-SiblingTool -ToolId 'list-resource-accounts' -ToolInput $passthrough
            [ordered]@{
                total               = $data.totalMatched
                attached            = $data.attachedOnThisPage
                unattached          = $data.unattachedOnThisPage
                byApplicationType   = Group-CountBy -Items $data.resourceAccounts -Property 'applicationType'
            }
        }

        $emergencyAddresses = Get-SnapshotSection -Section 'emergencyAddresses' -ScriptBlock {
            $data = Invoke-SiblingTool -ToolId 'list-emergency-addresses' -ToolInput $passthrough
            [ordered]@{
                total       = $data.totalMatched
                validated   = $data.validatedCount
                unvalidated = $data.unvalidatedCount
            }
        }

        $schedules = Get-SnapshotSection -Section 'schedules' -ScriptBlock {
            $data = Invoke-SiblingTool -ToolId 'get-schedules' -ToolInput $passthrough
            [ordered]@{ total = $data.totalMatched }
        }

        $policies = Get-SnapshotSection -Section 'policies' -ScriptBlock {
            $data = Invoke-SiblingTool -ToolId 'list-voice-policies' -ToolInput $passthrough
            [ordered]@{
                voiceRoutingPolicies = $data.voiceRoutingPolicyCount
                tenantDialPlans      = $data.tenantDialPlanCount
                teamsCallingPolicies = $data.teamsCallingPolicyCount
                voicemailPolicies    = $data.voicemailPolicyCount
            }
        }

        # No list tool exists for these two, so they are counted directly.
        $callQueues = Get-SnapshotSection -Section 'callQueues' -ScriptBlock {
            $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -ErrorAction Stop })
            [ordered]@{ total = $queues.Count }
        }

        $autoAttendants = Get-SnapshotSection -Section 'autoAttendants' -ScriptBlock {
            $attendants = @(Invoke-WithRetry -ScriptBlock { Get-CsAutoAttendant -ErrorAction Stop })
            [ordered]@{ total = $attendants.Count }
        }

        $after = [ordered]@{
            generatedAt        = (Get-Date).ToUniversalTime().ToString('o')
            phoneNumbers       = $phoneNumbers
            resourceAccounts   = $resourceAccounts
            callQueues         = $callQueues
            autoAttendants     = $autoAttendants
            emergencyAddresses = $emergencyAddresses
            schedules          = $schedules
            policies           = $policies
            warnings           = @($script:Warnings)
        }

        $numberTotal = if ($null -ne $phoneNumbers) { $phoneNumbers.total } else { 'unknown' }
        $queueTotal = if ($null -ne $callQueues) { $callQueues.total } else { 'unknown' }
        $attendantTotal = if ($null -ne $autoAttendants) { $autoAttendants.total } else { 'unknown' }
        $summary = "Tenant voice snapshot: $numberTotal phone numbers, $queueTotal call queues, $attendantTotal auto attendants, $($script:Warnings.Count) degraded sections."

        return (Write-StageResult -Summary $summary -After $after)
    }
    default {
        throw "Tool 'get-tenant-voice-snapshot' does not implement stage '$Stage'."
    }
}
