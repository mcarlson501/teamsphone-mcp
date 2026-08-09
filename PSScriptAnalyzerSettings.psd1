# PSScriptAnalyzer configuration for teamsphone-mcp.
#
# Applied by scripts/lint-tools.ps1 and by the PowerShell editor extension.
# The build gate fails on Error-severity findings only; Warning and Information
# findings are reported for review but do not block. See scripts/lint-tools.ps1.
@{
    IncludeDefaultRules = $true
    CustomRulePath      = @('scripts/analyzer/TeamsPhoneMcp.AnalyzerRules.psm1')
    Severity            = @('Error', 'Warning', 'Information')

    Rules               = @{
        PSUseCompatibleSyntax = @{
            Enable         = $true
            TargetVersions = @('7.4')
        }
    }
}
