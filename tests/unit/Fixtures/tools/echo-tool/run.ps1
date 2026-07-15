param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("snapshot", "preflight", "dryrun", "execute", "verify", "rollback")]
    [string]$Stage,

    [Parameter(Mandatory = $true)]
    [string]$InputJson
)

$parsed = $InputJson | ConvertFrom-Json
[pscustomobject]@{
    stage   = $Stage
    summary = "echo:$Stage"
    after   = [pscustomobject]@{
        echoedInput = $parsed.input
        snapshot    = $parsed.snapshot
    }
} | ConvertTo-Json -Depth 12 -Compress
