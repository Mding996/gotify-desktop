param([switch]$Offline, [string]$OfflineSource)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCommand) { throw 'Install the .NET 8 SDK and ensure dotnet is on PATH.' }
$cacheRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'GotifyDesktop-build-cache'
$env:DOTNET_CLI_HOME = Join-Path $cacheRoot 'cli-home'
$env:NUGET_PACKAGES = Join-Path $cacheRoot 'packages'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES | Out-Null
$restoreArguments = @()
if ($Offline) {
    if (-not $OfflineSource) { $OfflineSource = Join-Path $projectRoot '.offline-nuget' }
    $feed = (Resolve-Path -LiteralPath $OfflineSource -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $feed -PathType Container)) { throw 'OfflineSource must be a directory containing the required NuGet packages.' }
    $restoreArguments = @("-p:RestoreSources=$feed")
}
& $dotnetCommand.Source publish (Join-Path $projectRoot 'GotifyDesktop.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
    -p:PublishTrimmed=false "-p:RestoreConfigFile=$(Join-Path $projectRoot 'NuGet.Config')" @restoreArguments `
    -o (Join-Path $projectRoot 'publish')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
Write-Host "Created: $(Join-Path $projectRoot 'publish\GotifyDesktop.exe')"
