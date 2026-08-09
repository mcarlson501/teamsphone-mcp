BeforeAll {
    $script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:CustomRulePath = Join-Path $PSScriptRoot 'TeamsPhoneMcp.AnalyzerRules.psm1'

    function Invoke-CustomRule {
        param([Parameter(Mandatory)][string]$Script)

        $file = Join-Path ([System.IO.Path]::GetTempPath()) ("pssa-{0}.ps1" -f [guid]::NewGuid())
        try {
            Set-Content -LiteralPath $file -Value $Script -Encoding utf8
            @(Invoke-ScriptAnalyzer -Path $file -CustomRulePath $script:CustomRulePath -IncludeDefaultRules:$false)
        }
        finally {
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'Measure-TeamsPhoneUnsafeExecution' {
    It 'exposes the rule to PSScriptAnalyzer' {
        $rules = @(Get-ScriptAnalyzerRule -CustomRulePath $script:CustomRulePath)
        $rules.RuleName | Should -Contain 'Measure-TeamsPhoneUnsafeExecution'
    }

    It 'flags <Name> as an Error' -ForEach @(
        @{ Name = 'Invoke-Expression'; Script = 'Invoke-Expression "Get-Date"' }
        @{ Name = 'the iex alias'; Script = 'iex "Get-Date"' }
        @{ Name = 'Add-Type'; Script = 'Add-Type -TypeDefinition "public class C {}"' }
        @{ Name = 'Start-Process'; Script = 'Start-Process -FilePath /bin/sh' }
        @{ Name = 'the saps alias'; Script = 'saps /bin/sh' }
        @{ Name = 'the start alias'; Script = 'start /bin/sh' }
        @{ Name = 'an ampersand invocation'; Script = "& 'Start-Process' /bin/sh" }
        @{ Name = '[ScriptBlock]::Create'; Script = '$sb = [ScriptBlock]::Create("Get-Date"); & $sb' }
        @{ Name = 'the fully qualified scriptblock factory'; Script = '[System.Management.Automation.ScriptBlock]::Create("Get-Date")' }
        # Module qualification resolves to the same command, so it must not slip past.
        @{ Name = 'a module-qualified Start-Process'; Script = 'Microsoft.PowerShell.Management\Start-Process -FilePath /bin/sh' }
        @{ Name = 'a module-qualified Invoke-Expression'; Script = 'Microsoft.PowerShell.Utility\Invoke-Expression "Get-Date"' }
        @{ Name = 'a module-qualified Add-Type'; Script = 'Microsoft.PowerShell.Utility\Add-Type -TypeDefinition "public class C {}"' }
        @{ Name = 'a module-qualified alias'; Script = 'Microsoft.PowerShell.Utility\iex "Get-Date"' }
        @{ Name = 'a path-qualified Start-Process'; Script = '.\Start-Process' }
    ) {
        $findings = Invoke-CustomRule -Script $Script

        $findings | Should -Not -BeNullOrEmpty
        $findings[0].RuleName | Should -BeLike '*Measure-TeamsPhoneUnsafeExecution'
        [string]$findings[0].Severity | Should -Be 'Error'
    }

    It 'does not flag <Name>, which merely resembles a banned command' -ForEach @(
        @{ Name = 'a command ending in the banned name'; Script = 'Get-StartProcess' }
        @{ Name = 'a Teams cmdlet from a qualified module'; Script = 'MicrosoftTeams\Get-CsOnlineUser -Identity a@b.com' }
        @{ Name = 'a variable named after a banned command'; Script = '$startProcess = 1; Write-Output $startProcess' }
    ) {
        Invoke-CustomRule -Script $Script | Should -BeNullOrEmpty
    }

    It 'leaves ordinary tool code alone' {
        $findings = Invoke-CustomRule -Script @'
$user = Get-CsOnlineUser -Identity 'alice@contoso.com'
Write-Output $user.LineUri
'@

        $findings | Should -BeNullOrEmpty
    }

    It 'reports no violations across the shipped tool scripts' {
        $findings = @(Invoke-ScriptAnalyzer -Path (Join-Path $script:RepoRoot 'tools') -Recurse `
                -CustomRulePath $script:CustomRulePath -IncludeDefaultRules:$false)

        ($findings | ForEach-Object { "$($_.ScriptName):$($_.Line) $($_.Message)" }) -join "`n" | Should -BeNullOrEmpty
    }
}
