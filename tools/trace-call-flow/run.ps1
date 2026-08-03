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

$script:Nodes = [System.Collections.ArrayList]::new()
$script:Edges = [System.Collections.ArrayList]::new()
$script:Findings = [System.Collections.ArrayList]::new()
$script:NodeIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:EdgeIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:FindingIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:ExpandedConfigurations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Add-TraceNode {
    param([string]$Id, [string]$Type, [string]$Name, [string]$Status)
    if (-not $script:NodeIds.Add($Id)) { return }
    [void]$script:Nodes.Add([ordered]@{ id = $Id; type = $Type; name = $Name; status = $Status })
}

function Add-TraceEdge {
    param([string]$From, [string]$To, [string]$Relation)
    $key = "$From|$To|$Relation"
    if (-not $script:EdgeIds.Add($key)) { return }
    [void]$script:Edges.Add([ordered]@{ from = $From; to = $To; relation = $Relation })
}

function Add-TraceFinding {
    param([string]$Severity, [string]$Code, [string]$Identity, [string]$What, [string]$Why, [string]$Fix)
    $key = "$Code|$Identity"
    if (-not $script:FindingIds.Add($key)) { return }
    [void]$script:Findings.Add([ordered]@{
        severity = $Severity; code = $Code; identity = $Identity; what = $What; why = $Why; fix = $Fix
    })
}

function Resolve-UserTarget {
    param([string]$ObjectId, [string]$FromNodeId, [string]$Relation)
    $nodeId = "user:$ObjectId"
    try {
        $user = Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -Identity $ObjectId -ErrorAction Stop }
        $name = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName' -Default $ObjectId)
        Add-TraceNode -Id $nodeId -Type 'user' -Name $name -Status 'reachable'
    }
    catch {
        Add-TraceNode -Id $nodeId -Type 'user' -Name $ObjectId -Status 'unresolved'
        Add-TraceFinding -Severity 'critical' -Code 'targetUserNotFound' -Identity $ObjectId `
            -What "Call flow target user '$ObjectId' could not be resolved." `
            -Why 'Calls routed to a deleted or inaccessible user cannot complete.' `
            -Fix 'Update the auto attendant or call queue target to a valid user.'
    }
    Add-TraceEdge -From $FromNodeId -To $nodeId -Relation $Relation
}

function Resolve-CallTarget {
    param($Target, [string]$FromNodeId, [string]$Relation, [string[]]$ResourceAccountStack)
    if ($null -eq $Target) { return }
    $targetId = [string](Get-PropertyValue -InputObject $Target -Name 'Id')
    $targetType = [string](Get-PropertyValue -InputObject $Target -Name 'Type')
    if ([string]::IsNullOrWhiteSpace($targetId)) {
        Add-TraceFinding -Severity 'critical' -Code 'targetIdentityMissing' -Identity $FromNodeId `
            -What "A $targetType target from '$FromNodeId' has no identity." `
            -Why 'The call route cannot resolve a target without an identity.' `
            -Fix 'Replace the broken target in the source auto attendant or call queue.'
        return
    }

    switch -Regex ($targetType) {
        '^(?i)ApplicationEndpoint$' {
            Resolve-ResourceAccount -ObjectId $targetId -FromNodeId $FromNodeId -Relation $Relation -ResourceAccountStack $ResourceAccountStack
        }
        '^(?i)User$' { Resolve-UserTarget -ObjectId $targetId -FromNodeId $FromNodeId -Relation $Relation }
        '^(?i)Phone$' {
            $number = ConvertTo-E164Number -Value $targetId
            $normalizedId = if ($null -ne $number) { $number } else { $targetId }
            $nodeId = "externalNumber:$normalizedId"
            Add-TraceNode -Id $nodeId -Type 'externalNumber' -Name $normalizedId -Status 'terminal'
            Add-TraceEdge -From $FromNodeId -To $nodeId -Relation $Relation
        }
        '^(?i)Voicemail$' {
            $nodeId = "voicemail:$targetId"
            Add-TraceNode -Id $nodeId -Type 'voicemail' -Name $targetId -Status 'terminal'
            Add-TraceEdge -From $FromNodeId -To $nodeId -Relation $Relation
        }
        default {
            $nodeId = "target:$targetType`:$targetId"
            Add-TraceNode -Id $nodeId -Type $(if ([string]::IsNullOrWhiteSpace($targetType)) { 'unknown' } else { $targetType }) -Name $targetId -Status 'terminal'
            Add-TraceEdge -From $FromNodeId -To $nodeId -Relation $Relation
        }
    }
}

function Resolve-AutoAttendantFlow {
    param($Flow, [string]$AttendantNodeId, [string]$FlowLabel, [string[]]$ResourceAccountStack)
    if ($null -eq $Flow) { return }
    $menu = Get-PropertyValue -InputObject $Flow -Name 'Menu'
    if ($null -eq $menu) { return }
    $index = 0
    foreach ($option in @(Get-PropertyValue -InputObject $menu -Name 'MenuOptions' -Default @())) {
        $action = [string](Get-PropertyValue -InputObject $option -Name 'Action')
        $dtmf = [string](Get-PropertyValue -InputObject $option -Name 'DtmfResponse')
        $relation = "$FlowLabel / $dtmf / $action"
        $target = Get-PropertyValue -InputObject $option -Name 'CallTarget'
        if ($null -ne $target) {
            Resolve-CallTarget -Target $target -FromNodeId $AttendantNodeId -Relation $relation -ResourceAccountStack $ResourceAccountStack
        }
        else {
            $terminalId = "action:$AttendantNodeId`:$FlowLabel`:$index"
            Add-TraceNode -Id $terminalId -Type 'action' -Name $action -Status 'terminal'
            Add-TraceEdge -From $AttendantNodeId -To $terminalId -Relation $relation
        }
        $index++
    }
}

function Resolve-AutoAttendant {
    param([string]$ConfigurationId, [string]$ResourceAccountNodeId, [string[]]$ResourceAccountStack)
    $nodeId = "autoAttendant:$ConfigurationId"
    try {
        $attendants = @(Invoke-WithRetry -ScriptBlock { Get-CsAutoAttendant -Identity $ConfigurationId -ErrorAction Stop } | Where-Object { $null -ne $_ })
        if ($attendants.Count -eq 0) { throw "Auto attendant '$ConfigurationId' was not found." }
        $attendant = $attendants[0]
    }
    catch {
        Add-TraceNode -Id $nodeId -Type 'autoAttendant' -Name $ConfigurationId -Status 'unresolved'
        Add-TraceEdge -From $ResourceAccountNodeId -To $nodeId -Relation 'attached to'
        Add-TraceFinding -Severity 'critical' -Code 'autoAttendantNotFound' -Identity $ConfigurationId `
            -What "Resource account configuration '$ConfigurationId' does not resolve to an auto attendant." `
            -Why 'Calls reaching this resource account have no valid auto-attendant configuration.' `
            -Fix 'Repair the resource-account association or attach it to an existing auto attendant.'
        return
    }
    $name = [string](Get-PropertyValue -InputObject $attendant -Name 'Name' -Default $ConfigurationId)
    Add-TraceNode -Id $nodeId -Type 'autoAttendant' -Name $name -Status 'reachable'
    Add-TraceEdge -From $ResourceAccountNodeId -To $nodeId -Relation 'attached to'
    if (-not $script:ExpandedConfigurations.Add($nodeId)) { return }

    $operator = Get-PropertyValue -InputObject $attendant -Name 'Operator'
    if ($null -ne $operator) { Resolve-CallTarget -Target $operator -FromNodeId $nodeId -Relation 'operator' -ResourceAccountStack $ResourceAccountStack }
    Resolve-AutoAttendantFlow -Flow (Get-PropertyValue -InputObject $attendant -Name 'DefaultCallFlow') -AttendantNodeId $nodeId -FlowLabel 'default' -ResourceAccountStack $ResourceAccountStack
    foreach ($flow in @(Get-PropertyValue -InputObject $attendant -Name 'CallFlows' -Default @())) {
        $flowName = [string](Get-PropertyValue -InputObject $flow -Name 'Name' -Default 'scheduled')
        Resolve-AutoAttendantFlow -Flow $flow -AttendantNodeId $nodeId -FlowLabel $flowName -ResourceAccountStack $ResourceAccountStack
    }
}

function Resolve-CallQueue {
    param([string]$ConfigurationId, [string]$ResourceAccountNodeId, [string[]]$ResourceAccountStack)
    $nodeId = "callQueue:$ConfigurationId"
    try {
        $queues = @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -Identity $ConfigurationId -ErrorAction Stop } | Where-Object { $null -ne $_ })
        if ($queues.Count -eq 0) { throw "Call queue '$ConfigurationId' was not found." }
        $queue = $queues[0]
    }
    catch {
        Add-TraceNode -Id $nodeId -Type 'callQueue' -Name $ConfigurationId -Status 'unresolved'
        Add-TraceEdge -From $ResourceAccountNodeId -To $nodeId -Relation 'attached to'
        Add-TraceFinding -Severity 'critical' -Code 'callQueueNotFound' -Identity $ConfigurationId `
            -What "Resource account configuration '$ConfigurationId' does not resolve to a call queue." `
            -Why 'Calls reaching this resource account have no valid queue configuration.' `
            -Fix 'Repair the resource-account association or attach it to an existing call queue.'
        return
    }
    $name = [string](Get-PropertyValue -InputObject $queue -Name 'Name' -Default $ConfigurationId)
    Add-TraceNode -Id $nodeId -Type 'callQueue' -Name $name -Status 'reachable'
    Add-TraceEdge -From $ResourceAccountNodeId -To $nodeId -Relation 'attached to'
    if (-not $script:ExpandedConfigurations.Add($nodeId)) { return }

    $agents = @(Get-PropertyValue -InputObject $queue -Name 'Agents' -Default @())
    if ($agents.Count -eq 0) {
        Add-TraceFinding -Severity 'critical' -Code 'callQueueHasNoAgents' -Identity $ConfigurationId `
            -What "Call queue '$name' has no configured agents." `
            -Why 'Calls entering this queue cannot reach a person.' `
            -Fix "Use update-callqueue-members to add at least one reachable agent to '$name'."
    }
    foreach ($agent in $agents) {
        $agentId = [string](Get-PropertyValue -InputObject $agent -Name 'ObjectId')
        if ([string]::IsNullOrWhiteSpace($agentId)) { $agentId = [string]$agent }
        if ([string]::IsNullOrWhiteSpace($agentId)) { continue }
        $agentNodeId = "agent:$agentId"
        Add-TraceNode -Id $agentNodeId -Type 'agent' -Name $agentId -Status 'configured'
        Add-TraceEdge -From $nodeId -To $agentNodeId -Relation 'agent'
    }

    foreach ($targetSpec in @(
        @{ Property = 'OverflowActionTarget'; Relation = 'overflow' },
        @{ Property = 'TimeoutActionTarget'; Relation = 'timeout' }
    )) {
        $target = Get-PropertyValue -InputObject $queue -Name $targetSpec.Property
        if ($null -ne $target) { Resolve-CallTarget -Target $target -FromNodeId $nodeId -Relation $targetSpec.Relation -ResourceAccountStack $ResourceAccountStack }
    }
}

function Resolve-ResourceAccount {
    param([string]$ObjectId, [string]$FromNodeId, [string]$Relation, [string[]]$ResourceAccountStack)
    $nodeId = "resourceAccount:$ObjectId"
    if ($ResourceAccountStack -contains $ObjectId) {
        Add-TraceNode -Id $nodeId -Type 'resourceAccount' -Name $ObjectId -Status 'loop'
        Add-TraceEdge -From $FromNodeId -To $nodeId -Relation $Relation
        Add-TraceFinding -Severity 'critical' -Code 'callFlowLoopDetected' -Identity $ObjectId `
            -What "The call flow routes back to resource account '$ObjectId'." `
            -Why 'A routing loop can prevent calls from reaching a terminal destination.' `
            -Fix 'Change the AA/CQ target so the route terminates at a user, voicemail, phone number, or non-cyclic application.'
        return
    }
    try {
        $instances = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineApplicationInstance -Identity $ObjectId -ErrorAction Stop } | Where-Object { $null -ne $_ })
        if ($instances.Count -eq 0) { throw "Resource account '$ObjectId' was not found." }
        $instance = $instances[0]
    }
    catch {
        Add-TraceNode -Id $nodeId -Type 'resourceAccount' -Name $ObjectId -Status 'unresolved'
        Add-TraceEdge -From $FromNodeId -To $nodeId -Relation $Relation
        Add-TraceFinding -Severity 'critical' -Code 'resourceAccountNotFound' -Identity $ObjectId `
            -What "Application endpoint '$ObjectId' does not resolve to a resource account." `
            -Why 'Calls routed to a deleted resource account cannot continue.' `
            -Fix 'Replace the broken target with an existing resource account.'
        return
    }
    $name = [string](Get-PropertyValue -InputObject $instance -Name 'DisplayName' -Default $ObjectId)
    Add-TraceNode -Id $nodeId -Type 'resourceAccount' -Name $name -Status 'reachable'
    Add-TraceEdge -From $FromNodeId -To $nodeId -Relation $Relation

    try {
        $association = Invoke-WithRetry -ScriptBlock { Get-CsOnlineApplicationInstanceAssociation -Identity $ObjectId -ErrorAction Stop }
        if ($null -eq $association) { throw "Resource account '$ObjectId' is not associated." }
    }
    catch {
        Add-TraceFinding -Severity 'critical' -Code 'resourceAccountNotAssociated' -Identity $ObjectId `
            -What "Resource account '$name' is not associated with a call queue or auto attendant." `
            -Why 'The assigned number has no application configuration to receive the call.' `
            -Fix 'Attach the resource account to the intended call queue or auto attendant.'
        return
    }
    $configurationId = [string](Get-PropertyValue -InputObject $association -Name 'ConfigurationId')
    $configurationType = [string](Get-PropertyValue -InputObject $association -Name 'ConfigurationType')
    $nextStack = @($ResourceAccountStack) + $ObjectId
    if ($configurationType -match '(?i)AutoAttendant') {
        Resolve-AutoAttendant -ConfigurationId $configurationId -ResourceAccountNodeId $nodeId -ResourceAccountStack $nextStack
    }
    elseif ($configurationType -match '(?i)CallQueue') {
        Resolve-CallQueue -ConfigurationId $configurationId -ResourceAccountNodeId $nodeId -ResourceAccountStack $nextStack
    }
    else {
        Add-TraceFinding -Severity 'critical' -Code 'unknownApplicationAssociation' -Identity $ObjectId `
            -What "Resource account '$name' has unsupported association type '$configurationType'." `
            -Why 'The server cannot resolve the next call-flow step.' `
            -Fix 'Review the resource-account association in Teams admin center.'
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$dialedNumber = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $toolInput -Name 'dialedNumber'))

switch ($Stage) {
    'execute' {
        if ($null -eq $dialedNumber) { throw 'dialedNumber must be a valid E.164 phone number.' }
        $numberNodeId = "number:$dialedNumber"
        Add-TraceNode -Id $numberNodeId -Type 'phoneNumber' -Name $dialedNumber -Status 'input'
        $assignments = @(Invoke-WithRetry -ScriptBlock { Get-CsPhoneNumberAssignment -TelephoneNumber $dialedNumber -ErrorAction Stop } | Where-Object { $null -ne $_ })
        if ($assignments.Count -eq 0) {
            Add-TraceFinding -Severity 'critical' -Code 'phoneNumberNotFound' -Identity $dialedNumber `
                -What "Phone number '$dialedNumber' was not found in the tenant inventory." `
                -Why 'A number outside the tenant inventory has no Teams call flow to trace.' `
                -Fix 'Verify the dialed number or acquire it from the carrier before assigning it.'
        }
        else {
            $targetId = [string](Get-PropertyValue -InputObject $assignments[0] -Name 'AssignedPstnTargetId')
            if ([string]::IsNullOrWhiteSpace($targetId)) {
                Add-TraceFinding -Severity 'critical' -Code 'phoneNumberUnassigned' -Identity $dialedNumber `
                    -What "Phone number '$dialedNumber' is not assigned to a resource account." `
                    -Why 'Unassigned numbers cannot enter an auto attendant or call queue.' `
                    -Fix 'Assign the number to the intended resource account.'
            }
            else { Resolve-ResourceAccount -ObjectId $targetId -FromNodeId $numberNodeId -Relation 'assigned to' -ResourceAccountStack @() }
        }

        $severityRank = @{ critical = 0; warning = 1; info = 2 }
        $findings = @($script:Findings | Sort-Object -Property @(
            @{ Expression = { if ($severityRank.ContainsKey($_.severity)) { $severityRank[$_.severity] } else { 3 } } },
            @{ Expression = { $_.code } },
            @{ Expression = { $_.identity } }
        ))
        $after = [ordered]@{
            dialedNumber = $dialedNumber
            status = if ($findings.Count -eq 0) { 'resolved' } else { 'issuesFound' }
            nodes = @($script:Nodes | Sort-Object -Property { $_.id })
            edges = @($script:Edges | Sort-Object -Property { "$($_.from)|$($_.to)|$($_.relation)" })
            findings = $findings
        }
        return (Write-StageResult -Summary "Traced $dialedNumber across $($script:Nodes.Count) node(s) with $($findings.Count) finding(s)." -After $after)
    }
    default { throw "Tool 'trace-call-flow' does not implement stage '$Stage'." }
}