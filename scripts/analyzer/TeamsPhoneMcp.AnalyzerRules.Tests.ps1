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
        @{ Name = '[ScriptBlock]::Create'; Script = '$sb = [ScriptBlock]::Create("Get-Date"); & $sb' }
        @{ Name = 'the fully qualified scriptblock factory'; Script = '[System.Management.Automation.ScriptBlock]::Create("Get-Date")' }
    ) {
        $findings = Invoke-CustomRule -Script $Script

        $findings | Should -Not -BeNullOrEmpty
        $findings[0].RuleName | Should -BeLike '*Measure-TeamsPhoneUnsafeExecution'
        [string]$findings[0].Severity | Should -Be 'Error'
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
