param(
  [ValidateSet("win-x64", "win-arm64")]
  [string]$Runtime = "win-x64",
  [string]$Configuration = "Release",
  [string]$CertificatePath = "",
  [string]$CertificatePassword = "",
  [string]$CertificateSubject = "",
  [string]$ArtifactSigningMetadataPath = "",
  [string]$ArtifactSigningDlibPath = "",
  [string]$TimestampUrl = "http://timestamp.acs.microsoft.com",
  [string]$SigningDescription = "TriffView",
  [string]$SigningUrl = "https://triff.tools",
  [ValidateSet("PortableZip", "SingleFile", "Both")]
  [string]$PackageMode = "SingleFile",
  [switch]$SkipSign,
  [switch]$SkipDefenderScan,
  [switch]$SkipWebBuild,
  [switch]$CompressSingleFile,
  [switch]$NoCompression
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "native\TriffView.csproj"
$appDir = Join-Path $root "app"
$distDir = Join-Path $appDir "dist"
$assetDir = Join-Path $root "native\Assets"
$overlayZip = Join-Path $assetDir "overlay-dist.zip"
$singleFilePublishDir = Join-Path $root "artifacts\publish\$Runtime-single-file"
$portablePublishDir = Join-Path $root "artifacts\publish\$Runtime-portable"
$releaseDir = Join-Path $root "release"
$releaseExe = Join-Path $releaseDir "TriffView.exe"
$releaseZip = Join-Path $releaseDir "TriffView-$Runtime-portable.zip"
$localDotnet = Join-Path $root ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }
if ($CompressSingleFile -and $NoCompression) {
  throw "Use either -CompressSingleFile or -NoCompression, not both."
}
$enableCompression = if ($CompressSingleFile -and -not $NoCompression) { "true" } else { "false" }

function New-Directory($path) {
  New-Item -ItemType Directory -Force -Path $path | Out-Null
  return (Resolve-Path $path).Path
}

function Assert-UnderRoot($path) {
  $full = [System.IO.Path]::GetFullPath($path)
  $rootWithSlash = [System.IO.Path]::GetFullPath($root + [System.IO.Path]::DirectorySeparatorChar)
  if (-not $full.StartsWith($rootWithSlash, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to modify path outside TriffView: $full"
  }
  return $full
}

function Remove-DirectorySafe($path) {
  $full = Assert-UnderRoot $path
  if (Test-Path -LiteralPath $full) {
    Remove-Item -LiteralPath $full -Recurse -Force
  }
}

function Find-SignTool {
  $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }

  $windowsKits = "C:\Program Files (x86)\Windows Kits\10\bin"
  if (-not (Test-Path -LiteralPath $windowsKits)) { return $null }

  $tool = Get-ChildItem -LiteralPath $windowsKits -Recurse -Filter "signtool.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
  if ($tool) { return $tool.FullName }
  return $null
}

function Add-AzureCliToPath {
  if (Get-Command "az.cmd" -ErrorAction SilentlyContinue) { return }
  if (Get-Command "az" -ErrorAction SilentlyContinue) { return }

  $candidateDirs = @(
    "C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin",
    "C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin"
  )
  foreach ($dir in $candidateDirs) {
    $az = Join-Path $dir "az.cmd"
    if (Test-Path -LiteralPath $az) {
      $env:PATH = "$dir;$env:PATH"
      return
    }
  }
}

function Resolve-ExistingPath($path, $label) {
  if ([string]::IsNullOrWhiteSpace($path)) {
    throw "$label path is required."
  }
  if (-not (Test-Path -LiteralPath $path)) {
    throw "$label was not found: $path"
  }
  return (Resolve-Path -LiteralPath $path).Path
}

function Assert-NativeSuccess($name) {
  if ($LASTEXITCODE -ne 0) {
    throw "$name failed with exit code $LASTEXITCODE"
  }
}

function Test-PackageMode($mode) {
  return $PackageMode -eq $mode -or $PackageMode -eq "Both"
}

function Initialize-SignTool {
  if (-not $script:signingRequested) { return }

  $script:signTool = Find-SignTool
  if (-not $script:signTool) {
    throw "Signing was requested, but signtool.exe was not found. Install the Windows SDK or add signtool.exe to PATH."
  }
  if (-not [string]::IsNullOrWhiteSpace($ArtifactSigningMetadataPath)) {
    Add-AzureCliToPath
  }
}

function Sign-ReleaseExecutable($path) {
  if (-not $script:signingRequested) { return }

  $signArgs = @("sign", "/v", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/d", $SigningDescription, "/du", $SigningUrl)
  if (-not [string]::IsNullOrWhiteSpace($ArtifactSigningMetadataPath)) {
    $metadataPath = Resolve-ExistingPath $ArtifactSigningMetadataPath "Artifact Signing metadata"
    $dlibPath = Resolve-ExistingPath $ArtifactSigningDlibPath "Artifact Signing dlib"
    $signArgs += @("/dlib", $dlibPath, "/dmdf", $metadataPath)
  } elseif (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $signArgs += @("/f", $CertificatePath)
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
      $signArgs += @("/p", $CertificatePassword)
    }
  } else {
    $signArgs += @("/n", $CertificateSubject)
  }
  $signArgs += $path

  Write-Host "Signing $path..."
  & $script:signTool @signArgs
  Assert-NativeSuccess "signtool sign"
  & $script:signTool verify /pa /v $path
  Assert-NativeSuccess "signtool verify"
}

function Write-AuthenticodeStatus($path) {
  $signature = Get-AuthenticodeSignature -LiteralPath $path
  Write-Host "Authenticode status for $(Split-Path -Leaf $path): $($signature.Status)"
}

function Invoke-DefenderScan($path) {
  if ($SkipDefenderScan) { return }

  $scanCommand = Get-Command "Start-MpScan" -ErrorAction SilentlyContinue
  if ($scanCommand) {
    try {
      Write-Host "Running Microsoft Defender custom scan on $(Split-Path -Leaf $path)..."
      Start-MpScan -ScanType CustomScan -ScanPath $path
    } catch {
      Write-Warning "Microsoft Defender scan could not be completed: $($_.Exception.Message)"
    }
  } else {
    Write-Warning "Start-MpScan is not available; skipping Defender scan."
  }
}

function Write-ArtifactHash($path) {
  $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
  $fileName = Split-Path -Leaf $path
  $hashPath = Join-Path $releaseDir "$fileName.sha256.txt"
  "$($hash.Hash)  $fileName" | Set-Content -LiteralPath $hashPath -Encoding ascii
  return $hashPath
}

New-Directory (Join-Path $root ".dotnet-home") | Out-Null
New-Directory (Join-Path $root ".nuget") | Out-Null
New-Directory (Join-Path $root ".appdata\NuGet") | Out-Null
New-Directory (Join-Path $root ".nuget-cache") | Out-Null
New-Directory (Join-Path $root ".nuget-plugin-cache") | Out-Null
New-Directory $assetDir | Out-Null
New-Directory $releaseDir | Out-Null

$env:DOTNET_CLI_HOME = (Resolve-Path (Join-Path $root ".dotnet-home")).Path
$env:NUGET_PACKAGES = (Resolve-Path (Join-Path $root ".nuget")).Path
$env:APPDATA = (Resolve-Path (Join-Path $root ".appdata")).Path
$env:NUGET_HTTP_CACHE_PATH = (Resolve-Path (Join-Path $root ".nuget-cache")).Path
$env:NUGET_PLUGINS_CACHE_PATH = (Resolve-Path (Join-Path $root ".nuget-plugin-cache")).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

if ($SkipWebBuild) {
  Write-Host "Skipping TriffView web UI build; using existing app\dist output."
} else {
  Write-Host "Building TriffView web UI..."
  Push-Location $appDir
  try {
    & npm.cmd run build
    Assert-NativeSuccess "npm.cmd run build"
  } finally {
    Pop-Location
  }
}

$indexHtml = Join-Path $distDir "index.html"
if (-not (Test-Path -LiteralPath $indexHtml)) {
  throw "Vite build did not produce $indexHtml"
}

Write-Host "Bundling overlay UI..."
if (Test-Path -LiteralPath $overlayZip) {
  Remove-Item -LiteralPath $overlayZip -Force
}
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $overlayZip -Force

$script:signingRequested = (-not $SkipSign) -and (
  -not [string]::IsNullOrWhiteSpace($ArtifactSigningMetadataPath) -or
  -not [string]::IsNullOrWhiteSpace($CertificatePath) -or
  -not [string]::IsNullOrWhiteSpace($CertificateSubject)
)
$script:signTool = $null
Initialize-SignTool

if (-not $script:signingRequested -and -not $SkipSign) {
  Write-Warning "Unsigned release artifacts created. Pass Artifact Signing metadata/dlib paths, -CertificatePath, or -CertificateSubject to sign them."
}

$releaseArtifacts = @()

if (Test-PackageMode "PortableZip") {
  Write-Host "Publishing portable self-contained $Runtime folder..."
  Remove-DirectorySafe $portablePublishDir
  New-Directory $portablePublishDir | Out-Null

  & $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=embedded `
    -p:PublishReadyToRun=false `
    -o $portablePublishDir
  Assert-NativeSuccess "dotnet publish portable"

  $portableExe = Join-Path $portablePublishDir "TriffView.exe"
  if (-not (Test-Path -LiteralPath $portableExe)) {
    throw "Publish completed, but $portableExe was not produced."
  }

  Sign-ReleaseExecutable $portableExe
  Write-AuthenticodeStatus $portableExe

  if (Test-Path -LiteralPath $releaseZip) {
    Remove-Item -LiteralPath $releaseZip -Force
  }

  Write-Host "Creating portable ZIP..."
  Compress-Archive -Path (Join-Path $portablePublishDir "*") -DestinationPath $releaseZip -Force

  $hashPath = Write-ArtifactHash $releaseZip
  Invoke-DefenderScan $releaseZip
  $releaseArtifacts += [pscustomobject]@{
    Artifact = $releaseZip
    Hash = $hashPath
  }
}

if (Test-PackageMode "SingleFile") {
  Write-Host "Publishing self-contained single-file $Runtime executable..."
  Write-Host "Single-file compression enabled: $enableCompression"
  Remove-DirectorySafe $singleFilePublishDir
  New-Directory $singleFilePublishDir | Out-Null

  & $dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=$enableCompression `
    -p:DebugType=embedded `
    -p:PublishReadyToRun=false `
    -o $singleFilePublishDir
  Assert-NativeSuccess "dotnet publish single-file"

  $publishedExe = Join-Path $singleFilePublishDir "TriffView.exe"
  if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Publish completed, but $publishedExe was not produced."
  }

  try {
    Copy-Item -LiteralPath $publishedExe -Destination $releaseExe -Force
  } catch [System.IO.IOException] {
    throw "Could not replace $releaseExe because it is locked. Quit running TriffView instances from the tray, then rerun this script. The newly published exe is still available at $publishedExe"
  }

  Sign-ReleaseExecutable $releaseExe
  Write-AuthenticodeStatus $releaseExe

  $hashPath = Write-ArtifactHash $releaseExe
  Invoke-DefenderScan $releaseExe
  $releaseArtifacts += [pscustomobject]@{
    Artifact = $releaseExe
    Hash = $hashPath
  }
}

if ($releaseArtifacts.Count -eq 0) {
  throw "No release artifacts were produced for PackageMode=$PackageMode."
}

Write-Host ""
Write-Host "Release ready:"
foreach ($artifact in $releaseArtifacts) {
  Write-Host "  $($artifact.Artifact)"
  Write-Host "  $($artifact.Hash)"
}
Write-Host "Sizes:"
$releaseArtifacts |
  ForEach-Object { Get-Item -LiteralPath $_.Artifact } |
  Select-Object FullName, Length, LastWriteTime |
  Format-List
