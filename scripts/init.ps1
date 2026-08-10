$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/TeamsPhoneMcp.Host/TeamsPhoneMcp.Host.csproj'
$initArguments = @($args)

Push-Location $repoRoot
try {
    dotnet run --project $project --no-launch-profile -- init @initArguments
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $exitCode