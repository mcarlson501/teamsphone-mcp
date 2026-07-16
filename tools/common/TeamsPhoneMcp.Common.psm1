#requires -Version 7.4

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Shared helpers for TeamsPhone MCP tool scripts (build spec §6.2–6.3).

.DESCRIPTION
    Every tool `run.ps1` imports this module. It provides the stage input/output
    contract (exactly one JSON string per stage on the success stream), a
    throttling-aware retry wrapper, and a bounded polling helper. Nothing in this
    module writes to the success stream except Write-StageResult, so tool scripts
    must route all cmdlet output away from the pipeline (e.g. `$null = ...`).
#>

<#
.SYNOPSIS
    Parses the pipeline runner's -InputJson envelope into an object.
.OUTPUTS
    An object with `.input` (the canonical tool parameters) and `.snapshot`
    (the captured pre-execution state, or $null on the first stage).
#>
function Get-StageInput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InputJson
    )

    return ($InputJson | ConvertFrom-Json -Depth 32)
}

<#
.SYNOPSIS
    Emits the single JSON result string that the stage contract requires.
.DESCRIPTION
    The host reads exactly one JSON string from the success stream. Summary maps
    to the envelope summary, After to the diff's "after" state, and Checks to the
    preflight/verification check list.
#>
function Write-StageResult {
    [CmdletBinding()]
    param(
        [string]$Summary,
        [object]$After,
        [object[]]$Checks
    )

    $result = [ordered]@{}
    if ($PSBoundParameters.ContainsKey('Summary')) { $result['summary'] = $Summary }
    if ($PSBoundParameters.ContainsKey('After')) { $result['after'] = $After }
    if ($PSBoundParameters.ContainsKey('Checks')) { $result['checks'] = $Checks }

    return ($result | ConvertTo-Json -Depth 32 -Compress)
}

<#
.SYNOPSIS
    Detects a Microsoft Graph / Teams throttling (HTTP 429) response.
#>
function Test-IsThrottlingError {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $exception = $ErrorRecord.Exception
    $statusCode = $null
    $response = $exception.PSObject.Properties['Response']
    if ($null -ne $response -and $null -ne $response.Value) {
        $statusCode = $response.Value.PSObject.Properties['StatusCode']
    }

    if ($null -ne $statusCode -and [int]$statusCode.Value -eq 429) {
        return $true
    }

    return ($exception.Message -match '429|Too Many Requests|throttl')
}

<#
.SYNOPSIS
    Runs a script block, retrying only on throttling errors with exponential
    backoff plus jitter (build spec §6.3 resilience guidance).
#>
function Invoke-WithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$ScriptBlock,

        [int]$MaxAttempts = 5,
        [int]$BaseDelayMs = 500,
        [int]$MaxDelayMs = 20000
    )

    $attempt = 0
    while ($true) {
        $attempt++
        try {
            return (& $ScriptBlock)
        }
        catch {
            if ($attempt -ge $MaxAttempts -or -not (Test-IsThrottlingError -ErrorRecord $_)) {
                throw
            }

            $backoff = [Math]::Min($MaxDelayMs, $BaseDelayMs * [Math]::Pow(2, $attempt - 1))
            $jitter = Get-Random -Minimum 0 -Maximum $BaseDelayMs
            Start-Sleep -Milliseconds ([int]($backoff + $jitter))
        }
    }
}

<#
.SYNOPSIS
    Polls a condition until it is true or the timeout elapses. Returns $true if
    the condition was met, $false on timeout.
#>
function Wait-ForCondition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Condition,

        [int]$TimeoutSeconds = 60,
        [int]$PollIntervalSeconds = 5
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) {
            return $true
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }

    return $false
}

Export-ModuleMember -Function Get-StageInput, Write-StageResult, Test-IsThrottlingError, Invoke-WithRetry, Wait-ForCondition
