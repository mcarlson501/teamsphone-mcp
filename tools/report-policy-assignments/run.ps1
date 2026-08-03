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

function Get-PolicyValue {
    param($User, [string]$Primary, [string]$Fallback)
    $value = Get-PropertyValue -InputObject $User -Name $Primary
    if ($null -eq $value -and -not [string]::IsNullOrWhiteSpace($Fallback)) { $value = Get-PropertyValue -InputObject $User -Name $Fallback }
    return (Get-AssignedPolicyName -Value $value)
}

function ConvertTo-CsvCell {
    param([AllowNull()][object]$Value)
    $text = [string]$Value
    return '"' + $text.Replace('"', '""') + '"'
}

function ConvertTo-Report {
    param($Rows, [string]$Format)
    if ($Format -eq 'json') { return $null }
    if ($Format -eq 'csv') {
        $lines = @('userPrincipalName,voiceRoutingPolicy,tenantDialPlan,teamsCallingPolicy,callerIdPolicy,voicemailPolicy')
        foreach ($row in $Rows) {
            $lines += @(
                $row.userPrincipalName, $row.onlineVoiceRoutingPolicy, $row.tenantDialPlan,
                $row.teamsCallingPolicy, $row.callerIdPolicy, $row.voicemailPolicy
            ) | ForEach-Object { ConvertTo-CsvCell -Value $_ } | Join-String -Separator ','
        }
        return ($lines -join "`n")
    }
    $lines = @(
        '# Teams Phone policy assignments',
        '',
        '| User | Voice routing | Dial plan | Calling | Caller ID | Voicemail |',
        '|---|---|---|---|---|---|'
    )
    foreach ($row in $Rows) {
        $values = @($row.userPrincipalName, $row.onlineVoiceRoutingPolicy, $row.tenantDialPlan, $row.teamsCallingPolicy, $row.callerIdPolicy, $row.voicemailPolicy)
        $escaped = @($values | ForEach-Object { ([string]$_).Replace('|', '\|') })
        $lines += '| ' + ($escaped -join ' | ') + ' |'
    }
    return ($lines -join "`n")
}

$payload = Get-StageInput -InputJson $InputJson
$toolInput = Get-PropertyValue -InputObject $payload -Name 'input'
$pagination = Get-PropertyValue -InputObject $payload -Name 'pagination'
$format = [string](Get-PropertyValue -InputObject $toolInput -Name 'format' -Default 'json')

switch ($Stage) {
    'execute' {
        $users = @(Invoke-WithRetry -ScriptBlock { Get-CsOnlineUser -ResultSize ([int]::MaxValue) -ErrorAction Stop })
        $rows = @()
        foreach ($user in $users) {
            $rows += [ordered]@{
                userPrincipalName = [string](Get-PropertyValue -InputObject $user -Name 'UserPrincipalName')
                onlineVoiceRoutingPolicy = Get-PolicyValue -User $user -Primary 'OnlineVoiceRoutingPolicy' -Fallback ''
                tenantDialPlan = Get-PolicyValue -User $user -Primary 'TenantDialPlan' -Fallback ''
                teamsCallingPolicy = Get-PolicyValue -User $user -Primary 'TeamsCallingPolicy' -Fallback ''
                callerIdPolicy = Get-PolicyValue -User $user -Primary 'CallingLineIdentity' -Fallback 'CallingLineIdentityPolicy'
                voicemailPolicy = Get-PolicyValue -User $user -Primary 'OnlineVoicemailPolicy' -Fallback ''
            }
        }
        $ordered = @($rows | Sort-Object -Property { $_.userPrincipalName })
        $unassigned = [ordered]@{}
        foreach ($property in @('onlineVoiceRoutingPolicy', 'tenantDialPlan', 'teamsCallingPolicy', 'callerIdPolicy', 'voicemailPolicy')) {
            $unassigned[$property] = @($ordered | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.$property) }).Count
        }
        $result = Select-StagePage -Items $ordered -Pagination $pagination
        $report = ConvertTo-Report -Rows @($result.Items) -Format $format
        $after = [ordered]@{
            userCount = $ordered.Count
            format = $format
            unassignedCounts = $unassigned
            assignments = @($result.Items)
            report = $report
        }
        return (Write-StageResult -Summary "Reported voice policy assignments for $($ordered.Count) user(s); returning $($result.Page.returnedCount)." -After $after -Page $result.Page)
    }
    default { throw "Tool 'report-policy-assignments' does not implement stage '$Stage'." }
}