param(
  [ValidateSet("Slim", "Standalone")]
  [string]$Mode = "Standalone"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

$publishDir = if ($Mode -eq "Standalone") { ".\publish\Maxwell-version4" } else { ".\publish\win-x64-slim" }
$selfContained = if ($Mode -eq "Standalone") { "true" } else { "false" }
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $projectDir "publish"))
$publishFullPath = [System.IO.Path]::GetFullPath((Join-Path $projectDir $publishDir))
if (-not $publishFullPath.StartsWith(
    $publishRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar,
    [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to clean a publish path outside $publishRoot : $publishFullPath"
}
if (Test-Path -LiteralPath $publishFullPath -PathType Container) {
  Remove-Item -LiteralPath $publishFullPath -Recurse -Force
}

$properties = @(
  "-p:PublishSingleFile=true",
  "-p:DebugType=none",
  "-p:DebugSymbols=false"
)

if ($Mode -eq "Standalone") {
  $properties += "-p:EnableCompressionInSingleFile=true"
  $properties += "-p:IncludeNativeLibrariesForSelfExtract=true"
}

dotnet publish .\OpenRpaWorkflowLauncher.csproj `
  -c Release `
  -r win-x64 `
  --self-contained $selfContained `
  @properties `
  -o $publishDir

dotnet build .\Maxwell.RuntimeHost\Maxwell.RuntimeHost.csproj -c Release
$runtimeDir = Join-Path $publishDir "runtime"
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
Copy-Item .\Maxwell.RuntimeHost\bin\Release\net48\* $runtimeDir -Recurse -Force
Copy-Item .\install-browser-automation.ps1 $publishDir -Force

$openRpaRuntimeStaging = ".\runtime-staging"
if (Test-Path $openRpaRuntimeStaging -PathType Container) {
  Copy-Item (Join-Path $openRpaRuntimeStaging "*") $runtimeDir -Recurse -Force
  Copy-Item (Join-Path $openRpaRuntimeStaging "chromemanifest.json") (Join-Path $runtimeDir "chromemanifest.template.json") -Force
  # RuntimeHost owns its executable, configuration, and JSON protocol dependency.
  Copy-Item .\Maxwell.RuntimeHost\bin\Release\net48\* $runtimeDir -Recurse -Force

  # This distribution is explicitly win-x64. Keep the source staging tree
  # complete for development, but do not ship native binaries that Windows x64
  # can never load. This saves roughly 75 MB without changing workflow support.
  $excludedRuntimePaths = @(
    (Join-Path $runtimeDir "x86"),
    (Join-Path $runtimeDir "grpc_csharp_ext.x86.dll"),
    (Join-Path $runtimeDir "grpc_csharp_ext.x64.dylib"),
    (Join-Path $runtimeDir "libgrpc_csharp_ext.x64.so")
  )
  foreach ($excludedRuntimePath in $excludedRuntimePaths) {
    if (Test-Path -LiteralPath $excludedRuntimePath) {
      Remove-Item -LiteralPath $excludedRuntimePath -Recurse -Force
    }
  }
} else {
  Write-Warning "runtime-staging was not found. The package will run WWF built-in activities only."
  Write-Warning "Run ..\scripts\build-openrpa-runtime.ps1 on Windows before publishing OpenRPA activity support."
}

$browserExtensionSource = ".\BrowserExtension"
$browserExtensionDestination = Join-Path $runtimeDir "browser-extension"
if (-not (Test-Path (Join-Path $browserExtensionSource "manifest.json") -PathType Leaf)) {
  throw "The Maxwell MV3 browser extension is missing: $browserExtensionSource"
}
Copy-Item $browserExtensionSource $browserExtensionDestination -Recurse -Force

# Windows PowerShell 5.1 may decode a UTF-8 script without BOM using the
# system code page. Do not hard-code the Chinese assembly name here; the GUI
# is the large single-file executable emitted by dotnet publish.
$appExe = Get-ChildItem -Path $publishDir -Filter "*.exe" -File |
  Sort-Object Length -Descending |
  Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($appExe)) {
  throw "The GUI executable was not produced in $publishDir"
}
$runtimeExe = Join-Path $runtimeDir "Maxwell.RuntimeHost.exe"
$runtimeConfig = Join-Path $runtimeDir "Maxwell.RuntimeHost.exe.config"
$runtimeJson = Join-Path $runtimeDir "Newtonsoft.Json.dll"
$browserInstaller = Join-Path $publishDir "install-browser-automation.ps1"
$browserManifestTemplate = Join-Path $runtimeDir "chromemanifest.template.json"
$bundledBrowserExtensionManifest = Join-Path $browserExtensionDestination "manifest.json"
$requiredFiles = @($appExe, $runtimeExe, $runtimeConfig, $runtimeJson, $browserInstaller, $browserManifestTemplate, $bundledBrowserExtensionManifest)
foreach ($requiredFile in $requiredFiles) {
  if ([string]::IsNullOrWhiteSpace($requiredFile) -or -not (Test-Path $requiredFile -PathType Leaf)) {
    throw "Missing published file: $requiredFile"
  }
}

$zipFile = if ($Mode -eq "Standalone") { ".\publish\Maxwell-version4.zip" } else { ".\publish\Maxwell-win-x64-slim.zip" }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipFile -Force

Write-Host ""
Write-Host "Published app: $appExe"
Write-Host "Maxwell RuntimeHost: $runtimeExe"
Write-Host "Browser Native Messaging setup: $browserInstaller"
Write-Host "Bundled MV3 browser extension: $browserExtensionDestination"
Write-Host "Distribution package: $projectDir\$zipFile"
if ($Mode -eq "Slim") {
  Write-Host "The slim package requires .NET 8 Desktop Runtime."
}
