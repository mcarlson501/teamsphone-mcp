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

# Well-known first-party application ids that classify a resource account.
$script:ApplicationIds = @{
    '11cd3e2e-fccb-42ad-ad00-878b93575e07' = 'callQueue'
    'ce933385-9390-45d1-9512-c8d228074e07' = 'autoAttendant'
}

function Get-ApplicationType {
    param([string]$ApplicationId)

    if ([string]::IsNullOrWhiteSpace($ApplicationId)) { return 'unknown' }

    $normalized = $ApplicationId.ToLowerInvariant()
    if ($script:ApplicationIds.ContainsKey($normalized)) { return $script:ApplicationIds[$normalized] }

    return 'unknown'
}

function Get-Association {
    param([string]$ObjectId)

    # Association lookup is one call per account, so it is resolved for the
    # returned page only rather than for the whole tenant inventory.
    try {
        $association = Invoke-WithRetry -ScriptBlock {
            Get-CsOnlineApplicationInstanceAssociation -Identity $ObjectId -ErrorAction Stop
        }
    }
    catch {
        return $null
    }

    if ($null -eq $association) { return $null }

    return [ordered]@{
        configurationId   = [string](Get-PropertyValue -InputObject $association -Name 'ConfigurationId')
        configurationType = [string](Get-PropertyValue -InputObject $association -Name 'ConfigurationType')
    }
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$pagination = Get-PropertyValue -InputObject $payload -Name 'pagination'
$applicationType = [string](Get-PropertyValue -InputObject $toolInput -Name 'applicationType' -Default 'all')

switch ($Stage) {
    'execute' {
        # Tier-0 read: the pipeline invokes only the execute stage.
        $instances = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineApplicationInstance -ErrorAction Stop })

        $matched = @()
        foreach ($instance in $instances) {
            $instanceType = Get-ApplicationType -ApplicationId ([string](Get-PropertyValue -InputObject $instance -Name 'ApplicationId' -Default ''))
            if ($applicationType -ne 'all' -and $instanceType -ne $applicationType) { continue }

            $matched += [ordered]@{
                objectId          = [string](Get-PropertyValue -InputObject $instance -Name 'ObjectId')
                userPrincipalName = [string](Get-PropertyValue -InputObject $instance -Name 'UserPrincipalName')
                displayName       = [string](Get-PropertyValue -InputObject $instance -Name 'DisplayName')
                applicationType   = $instanceType
                applicationId     = [string](Get-PropertyValue -InputObject $instance -Name 'ApplicationId')
                phoneNumber       = ConvertTo-E164Number -Value ([string](Get-PropertyValue -InputObject $instance -Name 'PhoneNumber'))
            }
        }

        # Deterministic ordering keeps a continuation token pointing at the same
        # window across calls.
        $ordered = @($matched | Sort-Object -Property { "$($_.userPrincipalName)|$($_.objectId)" })
        $result = Select-StagePage -Items $ordered -Pagination $pagination

        $page = @()
        $attachedCount = 0
        foreach ($account in @($result.Items)) {
            $association = Get-Association -ObjectId $account.objectId
            $account['attached'] = $null -ne $association
            $account['association'] = $association
            if ($null -ne $association) { $attachedCount++ }
            $page += $account
        }

        $after = [ordered]@{
            totalMatched         = $result.TotalCount
            applicationType      = $applicationType
            attachedOnThisPage   = $attachedCount
            unattachedOnThisPage = $page.Count - $attachedCount
            resourceAccounts     = @($page)
        }

        $summary = "Matched $($result.TotalCount) resource accounts; returning $($result.Page.returnedCount) ($attachedCount attached)."
        return (Write-StageResult -Summary $summary -After $after -Page $result.Page)
    }
    default {
        throw "Tool 'list-resource-accounts' does not implement stage '$Stage'."
    }
}
