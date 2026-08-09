# Custom PSScriptAnalyzer rules for teamsphone-mcp (build spec §7, §15 S1).
#
# The ~40 tools/*/run.ps1 files are the project's real attack surface: they are
# community-contributed and, until S3 lands, execute in a FullLanguage runspace
# in the host process. These rules convert §7's "no generic execution tool"
# policy from prose into a build gate.

Set-StrictMode -Version Latest

# Command name (lower-cased, including aliases) -> canonical name reported.
$script:BannedCommands = @{
    'invoke-expression' = 'Invoke-Expression'
    'iex'               = 'Invoke-Expression'
    'add-type'          = 'Add-Type'
    'start-process'     = 'Start-Process'
    'saps'              = 'Start-Process'
    'start'             = 'Start-Process'
}

$script:BannedCommandReasons = @{
    'Invoke-Expression' = 'builds and runs code from a string, which turns tenant-controlled text into executable code'
    'Add-Type'          = 'compiles and loads arbitrary .NET into the host process, escaping every policy check'
    'Start-Process'     = 'spawns an unconstrained child process outside the audited execution pipeline'
}

function New-TeamsPhoneDiagnostic {
    [CmdletBinding()]
    [OutputType([Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord])]
    param(
        [Parameter(Mandatory)][string]$Message,
        [Parameter(Mandatory)][System.Management.Automation.Language.IScriptExtent]$Extent,
        [Parameter(Mandatory)][string]$RuleName
    )

    [Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord]@{
        Message  = $Message
        Extent   = $Extent
        RuleName = $RuleName
        Severity = 'Error'
    }
}

function Measure-TeamsPhoneUnsafeExecution {
    <#
    .SYNOPSIS
        Prohibits dynamic and unconstrained code execution in tool scripts.

    .DESCRIPTION
        Flags Invoke-Expression (and its iex alias), Add-Type, Start-Process
        (and its saps/start aliases), and [ScriptBlock]::Create. Each of these
        lets a tool run code the manifest never declared and the policy engine
        cannot inspect, which defeats the build spec's tool-surface guarantees.
        There is no approved use of these constructs in this repository; if a
        tool appears to need one, the capability belongs in the host instead.

    .PARAMETER ScriptBlockAst
        The script block AST supplied by PSScriptAnalyzer.

    .EXAMPLE
        Measure-TeamsPhoneUnsafeExecution -ScriptBlockAst $ast

    .INPUTS
        [System.Management.Automation.Language.ScriptBlockAst]

    .OUTPUTS
        [Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord[]]
    #>
    [CmdletBinding()]
    [OutputType([Microsoft.Windows.PowerShell.ScriptAnalyzer.Generic.DiagnosticRecord[]])]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNull()]
        [System.Management.Automation.Language.ScriptBlockAst]$ScriptBlockAst
    )

    $ruleName = $PSCmdlet.MyInvocation.InvocationName

    $commands = $ScriptBlockAst.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
        $true)

    foreach ($command in $commands) {
        $name = $command.GetCommandName()
        if ([string]::IsNullOrEmpty($name)) {
            continue
        }

        $canonical = $script:BannedCommands[$name.ToLowerInvariant()]
        if ($null -eq $canonical) {
            continue
        }

        New-TeamsPhoneDiagnostic -RuleName $ruleName -Extent $command.Extent -Message (
            "'$name' is not permitted in tool scripts: $($script:BannedCommandReasons[$canonical]).")
    }

    $scriptBlockFactories = $ScriptBlockAst.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
            $node.Expression -is [System.Management.Automation.Language.TypeExpressionAst] -and
            $node.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Member.Value -eq 'Create' -and
            $node.Expression.TypeName.Name -match '(^|\.)scriptblock$'
        },
        $true)

    foreach ($factory in $scriptBlockFactories) {
        New-TeamsPhoneDiagnostic -RuleName $ruleName -Extent $factory.Extent -Message (
            "'[ScriptBlock]::Create' is not permitted in tool scripts: it compiles code from a string, " +
            'which turns tenant-controlled text into executable code.')
    }
}

Export-ModuleMember -Function Measure-TeamsPhoneUnsafeExecution
