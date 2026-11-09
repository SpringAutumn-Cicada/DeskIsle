$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet-win/dotnet.exe'
$publish = Join-Path $root 'artifacts/windows-checks'
$results = Join-Path $root 'artifacts/test-results/windows-components.json'
$localRun = Join-Path ([IO.Path]::GetTempPath()) ('DeskIsle-component-run-' + [Guid]::NewGuid())
Push-Location $root
try {
    & $dotnet publish tests/DeskIsle.WindowsChecks/DeskIsle.WindowsChecks.csproj -c Release -r win-x64 --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Component harness publish failed' }
    New-Item -ItemType Directory -Path $localRun | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $localRun -Recurse
    & (Join-Path $localRun 'DeskIsle.WindowsChecks.exe') $results
    if ($LASTEXITCODE -ne 0) { throw 'Windows component checks failed; inspect artifacts/test-results/windows-components.json' }
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $localRun) { Remove-Item -LiteralPath $localRun -Recurse -Force }
}
