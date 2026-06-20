Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "native\TriffView.csproj"
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }

New-Item -ItemType Directory -Force `
  (Join-Path $root ".dotnet-home"), `
  (Join-Path $root ".nuget"), `
  (Join-Path $root ".appdata\NuGet"), `
  (Join-Path $root ".nuget-cache"), `
  (Join-Path $root ".nuget-plugin-cache") | Out-Null

$env:DOTNET_CLI_HOME = (Resolve-Path (Join-Path $root ".dotnet-home")).Path
$env:NUGET_PACKAGES = (Resolve-Path (Join-Path $root ".nuget")).Path
$env:APPDATA = (Resolve-Path (Join-Path $root ".appdata")).Path
$env:NUGET_HTTP_CACHE_PATH = (Resolve-Path (Join-Path $root ".nuget-cache")).Path
$env:NUGET_PLUGINS_CACHE_PATH = (Resolve-Path (Join-Path $root ".nuget-plugin-cache")).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

& $dotnet build $project
