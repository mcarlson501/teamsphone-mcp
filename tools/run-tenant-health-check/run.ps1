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

$script:Findings = [System.Collections.ArrayList]::new()
$script:Warnings = [System.Collections.ArrayList]::new()

function Invoke-SiblingTool {
    param([string]$ToolId, [hashtable]$ToolInput)
    $scriptPath = Join-Path $PSScriptRoot '..' $ToolId 'run.ps1'
    $payload = @{ input = $ToolInput; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 6
    $output = & $scriptPath -Stage execute -InputJson $payload
    return ($output | ConvertFrom-Json).after
}

function Get-HealthSection {
    param([string]$Name, [scriptblock]$ScriptBlock)
    try { return (& $ScriptBlock) }
    catch {
        [void]$script:Warnings.Add([ordered]@{ section = $Name; message = $_.Exception.Message })
        return $null
    }
}

function Add-Finding {
    param([string]$Section, [string]$Severity, [string]$Code, [string]$What, [string]$Why, [string]$Fix)
    [void]$script:Findings.Add([ordered]@{
        section = $Section; severity = $Severity; code = $Code; what = $What; why = $Why; fix = $Fix
    })
}

function Add-ReportFindings {
    param([string]$Section, $Report)
    if ($null -eq $Report) { return }
    foreach ($finding in @(Get-PropertyValue -InputObject $Report -Name 'findings' -Default @())) {
        Add-Finding -Section $Section `
            -Severity ([string](Get-PropertyValue -InputObject $finding -Name 'severity')) `
            -Code ([string](Get-PropertyValue -InputObject $finding -Name 'code')) `
            -What ([string](Get-PropertyValue -InputObject $finding -Name 'what')) `
            -Why ([string](Get-PropertyValue -InputObject $finding -Name 'why')) `
            -Fix ([string](Get-PropertyValue -InputObject $finding -Name 'fix'))
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$passthrough = @{
    tenantId = [string](Get-PropertyValue -InputObject $toolInput -Name 'tenantId')
    credentialRef = [string](Get-PropertyValue -InputObject $toolInput -Name 'credentialRef')
}

switch ($Stage) {
    'execute' {
        $numberUtilization = Get-HealthSection -Name 'numberUtilization' -ScriptBlock {
            Invoke-SiblingTool -ToolId 'report-number-utilization' -ToolInput $passthrough
        }
        Add-ReportFindings -Section 'numberUtilization' -Report $numberUtilization

        $licenseUtilization = Get-HealthSection -Name 'licenseUtilization' -ScriptBlock {
            Invoke-SiblingTool -ToolId 'report-license-utilization' -ToolInput $passthrough
        }
        Add-ReportFindings -Section 'licenseUtilization' -Report $licenseUtilization

        $emergencyCoverage = Get-HealthSection -Name 'emergencyCoverage' -ScriptBlock {
            Invoke-SiblingTool -ToolId 'report-emergency-coverage' -ToolInput $passthrough
        }
        Add-ReportFindings -Section 'emergencyCoverage' -Report $emergencyCoverage

        $resourceAccounts = Get-HealthSection -Name 'resourceAccounts' -ScriptBlock {
            Invoke-SiblingTool -ToolId 'list-resource-accounts' -ToolInput $passthrough
        }
        if ($null -ne $resourceAccounts) {
            $unattached = @($resourceAccounts.resourceAccounts | Where-Object { -not $_.attached })
            if ($unattached.Count -gt 0) {
                Add-Finding -Section 'resourceAccounts' -Severity 'warning' -Code 'unattachedResourceAccounts' `
                    -What "$($unattached.Count) resource account(s) are not attached to a call queue or auto attendant." `
                    -Why 'Unattached resource accounts consume administrative inventory and may indicate an incomplete or deleted call flow.' `
                    -Fix 'Review list-resource-accounts and attach each account to its intended application or remove it.'
            }
        }

        $callQueues = Get-HealthSection -Name 'callQueues' -ScriptBlock {
            @(Invoke-WithRetry -ScriptBlock { Get-CsCallQueue -ErrorAction Stop })
        }
        if ($null -ne $callQueues) {
            foreach ($queue in $callQueues) {
                $agents = @(Get-PropertyValue -InputObject $queue -Name 'Agents' -Default @())
                if ($agents.Count -eq 0) {
                    $name = [string](Get-PropertyValue -InputObject $queue -Name 'Name')
                    if ([string]::IsNullOrWhiteSpace($name)) { $name = [string](Get-PropertyValue -InputObject $queue -Name 'Identity') }
                    Add-Finding -Section 'callQueues' -Severity 'critical' -Code 'callQueueHasNoAgents' `
                        -What "Call queue '$name' has no configured agents." `
                        -Why 'Calls entering this queue cannot reach a person.' `
                        -Fix "Use update-callqueue-members to add at least one reachable agent to '$name'."
                }
            }
        }

        $severityRank = @{ critical = 0; warning = 1; info = 2 }
        $orderedFindings = @($script:Findings | Sort-Object -Property @(
            @{ Expression = { if ($severityRank.ContainsKey($_.severity)) { $severityRank[$_.severity] } else { 3 } } },
            @{ Expression = { $_.section } },
            @{ Expression = { $_.code } }
        ))
        $criticalCount = @($orderedFindings | Where-Object { $_.severity -eq 'critical' }).Count
        $warningCount = @($orderedFindings | Where-Object { $_.severity -eq 'warning' }).Count
        $overallStatus = if ($criticalCount -gt 0) {
            'criticalIssues'
        }
        elseif ($warningCount -gt 0) {
            'issuesFound'
        }
        elseif ($script:Warnings.Count -gt 0) {
            'degraded'
        }
        else { 'healthy' }

        $after = [ordered]@{
            checkedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            overallStatus = $overallStatus
            findingCounts = [ordered]@{ critical = $criticalCount; warning = $warningCount; total = $orderedFindings.Count }
            findings = $orderedFindings
            warnings = @($script:Warnings)
            sections = [ordered]@{
                numberUtilization = $numberUtilization
                licenseUtilization = $licenseUtilization
                emergencyCoverage = $emergencyCoverage
                resourceAccounts = $resourceAccounts
                callQueueCount = if ($null -eq $callQueues) { $null } else { $callQueues.Count }
            }
        }
        return (Write-StageResult -Summary "Tenant health check found $criticalCount critical and $warningCount warning issue(s); $($script:Warnings.Count) section(s) degraded." -After $after)
    }
    default { throw "Tool 'run-tenant-health-check' does not implement stage '$Stage'." }
}