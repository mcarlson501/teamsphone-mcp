#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Lints the PowerShell surface of the repository (build spec §15 S1).

.DESCRIPTION
    Runs PSScriptAnalyzer with the repository settings, including the custom
    rules in scripts/analyzer that ban dynamic and unconstrained execution in
    tool scripts. Error-severity findings fail the run; Warning and Information
    findings are printed for review.

.PARAMETER Path
    Directories or files to analyze. Defaults to tools/ and scripts/.

.PARAMETER FailOn
    Lowest severity that fails the run. Defaults to Error.

.EXAMPLE
    ./scripts/lint-tools.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Path,
    [ValidateSet('Error', 'Warning', 'Information')]
    [string]$FailOn = 'Error'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    if (-not $Path) {
        $Path = @('tools', 'scripts')
    }

    $settingsPath = Join-Path $repoRoot 'PSScriptAnalyzerSettings.psd1'
    $customRulePath = Join-Path $repoRoot 'scripts/analyzer/TeamsPhoneMcp.AnalyzerRules.psm1'

    # A typo in the custom rule module would silently turn the gate into a no-op.
    $customRules = @(Get-ScriptAnalyzerRule -CustomRulePath $customRulePath -ErrorAction Stop)
    if ($customRules.Count -eq 0) {
        throw "No custom rules were loaded from '$customRulePath'. The security gate would not run."
    }
    Write-Host "Custom rules loaded: $(($customRules.RuleName | Sort-Object) -join ', ')"

    # PSScriptAnalyzer occasionally throws a NullReferenceException from inside its own
    # engine on a cold CI runner. Findings come back as objects rather than errors, so a
    # bounded retry cannot hide a real violation — it only survives the crash.
    $findings = @($Path | ForEach-Object {
            $target = $_
            for ($attempt = 1; ; $attempt++) {
                try {
                    Invoke-ScriptAnalyzer -Path $target -Recurse -Settings $settingsPath
                    break
                }
                catch {
                    if ($attempt -ge 3) {
                        throw "PSScriptAnalyzer failed on '$target' after $attempt attempts: $_"
                    }

                    Write-Host "PSScriptAnalyzer threw on '$target' (attempt $attempt); retrying: $_"
                }
            }
        })

    $order = @{ Information = 0; Warning = 1; Error = 2; ParseError = 3 }
    $threshold = $order[$FailOn]
    $blocking = @($findings | Where-Object { $order[[string]$_.Severity] -ge $threshold })

    if ($findings.Count -gt 0) {
        $findings |
            Group-Object Severity, RuleName |
            Sort-Object { $order[[string]$_.Group[0].Severity] } -Descending |
            ForEach-Object { '{0,5}  {1}' -f $_.Count, $_.Name } |
            Write-Host
        Write-Host ''
    }

    if ($blocking.Count -gt 0) {
        Write-Host "$FailOn-or-higher findings:" -ForegroundColor Red
        foreach ($finding in $blocking) {
            $relative = [System.IO.Path]::GetRelativePath($repoRoot, $finding.ScriptPath)
            Write-Host ("  {0}:{1} [{2}] {3}" -f $relative, $finding.Line, $finding.RuleName, $finding.Message)
        }
        Write-Host ''
        Write-Host "PSScriptAnalyzer reported $($blocking.Count) $FailOn-or-higher finding(s)." -ForegroundColor Red
        Pop-Location
        exit 1
    }

    Write-Host "PSScriptAnalyzer: no $FailOn-or-higher findings across $($Path -join ', ')." -ForegroundColor Green
}
finally {
    if ((Get-Location -Stack).Count -gt 0) {
        Pop-Location
    }
}
