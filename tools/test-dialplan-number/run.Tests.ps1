BeforeAll {
    $script:RunScript = Join-Path $PSScriptRoot 'run.ps1'

    function Get-CsEffectiveTenantDialPlan {
        param([string]$Identity, [string]$ErrorAction)
    }
    function Test-CsEffectiveTenantDialPlan {
        param([string]$Identity, [string]$DialedNumber, [string]$CallerNumber, [string]$ErrorAction)
    }
    function New-StageInput {
        param([string]$DialedNumber = '4255550100', [string]$CallerNumber = $null)

        $input = @{ userUpn = 'jdoe@contoso.com'; dialedNumber = $DialedNumber }
        if ($null -ne $CallerNumber) { $input.callerNumber = $CallerNumber }
        return (@{ input = $input; snapshot = $null; pagination = $null } | ConvertTo-Json -Depth 5)
    }
}

Describe 'test-dialplan-number' {
    BeforeEach {
        Mock Get-CsEffectiveTenantDialPlan {
            [PSCustomObject]@{ Identity = 'Tag:US-Seattle' }
        }
        Mock Test-CsEffectiveTenantDialPlan {
            [PSCustomObject]@{
                TranslatedNumber = '+14255550100'
                MatchingRule = 'US ten digit'
            }
        }
    }

    It 'returns the effective plan, matched rule, and normalized number' {
        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput) | ConvertFrom-Json

        $parsed.after.effectiveDialPlan | Should -Be 'Tag:US-Seattle'
        $parsed.after.normalizedNumber | Should -Be '+14255550100'
        $parsed.after.matchingRule | Should -Be 'US ten digit'
        $parsed.after.matched | Should -BeTrue
        $parsed.after.findings | Should -HaveCount 0
    }

    It 'passes an optional caller number to caller-specific normalization' {
        $null = & $script:RunScript -Stage execute -InputJson (New-StageInput -CallerNumber '+14255550199')

        Should -Invoke Test-CsEffectiveTenantDialPlan -Times 1 -Exactly -ParameterFilter {
            $Identity -eq 'jdoe@contoso.com' -and
            $DialedNumber -eq '4255550100' -and
            $CallerNumber -eq '+14255550199'
        }
    }

    It 'returns an actionable finding when no rule matches' {
        Mock Test-CsEffectiveTenantDialPlan { [PSCustomObject]@{} }

        $parsed = & $script:RunScript -Stage execute -InputJson (New-StageInput -DialedNumber '123') | ConvertFrom-Json

        $parsed.after.matched | Should -BeFalse
        $parsed.after.normalizedNumber | Should -BeNullOrEmpty
        $parsed.after.findings | Should -HaveCount 1
        $parsed.after.findings[0].code | Should -Be 'numberDidNotNormalize'
        $parsed.after.findings[0].fix | Should -Not -BeNullOrEmpty
    }

    It 'retries a throttled dial plan read' {
        $global:DialPlanAttempts = 0
        Mock Get-CsEffectiveTenantDialPlan {
            $global:DialPlanAttempts++
            if ($global:DialPlanAttempts -eq 1) { throw 'Response status code 429 (Too Many Requests).' }
            [PSCustomObject]@{ Identity = 'Tag:US-Seattle' }
        }

        $null = & $script:RunScript -Stage execute -InputJson (New-StageInput)

        $global:DialPlanAttempts | Should -Be 2
    }

    It 'throws for an unsupported stage' {
        { & $script:RunScript -Stage verify -InputJson (New-StageInput) } | Should -Throw
    }
}