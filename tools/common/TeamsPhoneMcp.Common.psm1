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
    An object with `.input` (the canonical tool parameters), `.snapshot`
    (the captured pre-execution state, or $null on the first stage), and
    `.pagination` (host-validated page size/offset, or $null for detail tools).
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
        [object[]]$Checks,
        [object]$Page
    )

    $result = [ordered]@{}
    if ($PSBoundParameters.ContainsKey('Summary')) { $result['summary'] = $Summary }
    if ($PSBoundParameters.ContainsKey('After')) { $result['after'] = $After }
    if ($PSBoundParameters.ContainsKey('Checks')) { $result['checks'] = $Checks }
    if ($PSBoundParameters.ContainsKey('Page')) { $result['page'] = $Page }

    return ($result | ConvertTo-Json -Depth 32 -Compress)
}

<#
.SYNOPSIS
    Emits the single JSON result string for the `snapshot` stage.
.DESCRIPTION
    Unlike every other stage, the snapshot stage's output *is* the captured
    state: the host stores it verbatim as the envelope's `diff.before` and
    threads it back to later stages as `.snapshot`. So it is emitted as a bare
    object rather than wrapped in the summary/after/checks envelope.
#>
function Write-StageSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$State
    )

    return ($State | ConvertTo-Json -Depth 32 -Compress)
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

<#
.SYNOPSIS
    Reads a property from an object without tripping Set-StrictMode when the
    property is absent.
.DESCRIPTION
    Teams cmdlet output shape varies by module version and telephony model, so
    tools must treat every non-guaranteed property as optional.
#>
function Get-PropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [object]$Default = $null
    )

    if ($null -eq $InputObject) { return $Default }

    if ($InputObject -is [System.Collections.IDictionary]) {
        if ($InputObject.Contains($Name)) { return $InputObject[$Name] }
        return $Default
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }

    return $property.Value
}

function Test-TeamsEmergencyLocationValidated {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Location
    )

    if ($null -eq $Location) { return $false }

    $validationStatus = [string](Get-PropertyValue -InputObject $Location -Name 'ValidationStatus')
    if (-not [string]::IsNullOrWhiteSpace($validationStatus)) {
        return [string]::Equals($validationStatus, 'Validated', [System.StringComparison]::OrdinalIgnoreCase)
    }

    return [bool](Get-PropertyValue -InputObject $Location -Name 'IsValidated' -Default $false)
}

<#
.SYNOPSIS
    Returns $true when the supplied value is a GUID.
.DESCRIPTION
    Used by tools that accept either a friendly name or an object identity so
    they can pick the right lookup cmdlet parameter.
#>
function Test-IsGuid {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }

    $parsed = [Guid]::Empty
    return [Guid]::TryParse($Value, [ref]$parsed)
}

<#
.SYNOPSIS
    Extracts the policy name from an assigned-policy value.
.DESCRIPTION
    Get-CsOnlineUser returns policy assignments either as a plain string or as an
    object carrying a Name property; $null means the tenant default applies.
#>
function Get-AssignedPolicyName {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return $Value }

    $name = Get-PropertyValue -InputObject $Value -Name 'Name'
    if ($null -ne $name) { return [string]$name }

    return [string]$Value
}

<#
.SYNOPSIS
    Normalizes a telephone number or tel: URI to bare E.164 form.
.OUTPUTS
    "+15551234567", or $null when the value is empty or not E.164-shaped. Any
    extension suffix (";ext=123") is dropped.
#>
function ConvertTo-E164Number {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }

    $candidate = $Value.Trim()
    if ($candidate -match '^(?i)tel:') { $candidate = $candidate.Substring(4) }

    $extensionIndex = $candidate.IndexOf(';')
    if ($extensionIndex -ge 0) { $candidate = $candidate.Substring(0, $extensionIndex) }

    $candidate = ($candidate -replace '[\s\-\(\)\.]', '')
    if ($candidate -notmatch '^\+[1-9]\d{6,14}$') { return $null }

    return $candidate
}

<#
.SYNOPSIS
    Applies the host-issued page window to a fully materialized, stably ordered
    collection and produces the `page` metadata the pipeline requires.
.DESCRIPTION
    The host owns paging state: it validates pageSize, decodes the continuation
    token into an offset, and re-issues the next token from `nextOffset`. Tools
    therefore only slice. Callers must sort deterministically before slicing so a
    continuation token keeps pointing at the same window.
.OUTPUTS
    An object with `.Items` (the page), `.Page` (returnedCount/hasMore/nextOffset)
    and `.TotalCount` (the pre-paging match count).
#>
function Select-StagePage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$Items,

        [AllowNull()]
        [object]$Pagination
    )

    $all = @($Items | Where-Object { $null -ne $_ })
    $total = $all.Count

    $offset = 0
    $pageSize = $total
    if ($null -ne $Pagination) {
        $requestedSize = Get-PropertyValue -InputObject $Pagination -Name 'pageSize'
        $requestedOffset = Get-PropertyValue -InputObject $Pagination -Name 'offset'
        if ($null -ne $requestedSize) { $pageSize = [int]$requestedSize }
        if ($null -ne $requestedOffset) { $offset = [int]$requestedOffset }
    }

    if ($offset -lt 0) { $offset = 0 }
    if ($pageSize -lt 0) { $pageSize = 0 }

    $slice = @()
    if ($offset -lt $total -and $pageSize -gt 0) {
        $lastIndex = [Math]::Min($total, $offset + $pageSize) - 1
        $slice = @($all[$offset..$lastIndex])
    }

    $consumed = $offset + $slice.Count
    $hasMore = $consumed -lt $total

    $page = [ordered]@{
        returnedCount = $slice.Count
        hasMore       = $hasMore
    }
    if ($hasMore) { $page['nextOffset'] = $consumed }

    return [pscustomobject]@{
        Items      = $slice
        Page       = $page
        TotalCount = $total
    }
}

Export-ModuleMember -Function Get-StageInput, Write-StageResult, Write-StageSnapshot, Test-IsThrottlingError, Invoke-WithRetry, Wait-ForCondition, Get-PropertyValue, Test-TeamsEmergencyLocationValidated, Test-IsGuid, Get-AssignedPolicyName, ConvertTo-E164Number, Select-StagePage
