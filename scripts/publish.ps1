$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet-win/dotnet.exe'
if (!(Test-Path $dotnet)) { throw 'Install the pinned Windows SDK into .tools/dotnet-win first; see README.md.' }
Push-Location $root
try {
    & $dotnet publish src/DeskIsle/DeskIsle.csproj -c Release -r win-x64 --self-contained true -o artifacts/DeskIsle-win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    [xml]$project = Get-Content src/DeskIsle/DeskIsle.csproj
    $version = $project.Project.PropertyGroup.Version
    Compress-Archive -Path artifacts/DeskIsle-win-x64 -DestinationPath ("artifacts/DeskIsle-$version-win-x64.zip") -Force
} finally { Pop-Location }
