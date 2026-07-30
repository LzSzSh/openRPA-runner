param(
  [ValidateSet("Slim", "Standalone", "Bundled", "LocalBrowser", "Shared")]
  [string]$Mode = "Bundled"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

$publishDir = switch ($Mode) {
  "Bundled" { ".\publish\Maxwell-version4-bundled" }
  "LocalBrowser" { ".\publish\Maxwell-version4-local-browser" }
  "Shared" { ".\publish\Maxwell-shared-direct" }
  "Standalone" { ".\publish\Maxwell-version4" }
  default { ".\publish\win-x64-slim" }
}
$selfContained = if ($Mode -eq "Slim") { "false" } else { "true" }
$browserMode = if ($Mode -eq "LocalBrowser") { "LocalOnly" } elseif ($Mode -eq "Bundled") { "BundledOnly" } else { "Both" }
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

if ($selfContained -eq "true") {
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
Copy-Item .\install-openrpa-extension.ps1 $publishDir -Force

$openRpaRuntimeStaging = ".\runtime-staging"
if (Test-Path $openRpaRuntimeStaging -PathType Container) {
  Copy-Item (Join-Path $openRpaRuntimeStaging "*") $runtimeDir -Recurse -Force
  Copy-Item (Join-Path $openRpaRuntimeStaging "chromemanifest.json") (Join-Path $runtimeDir "chromemanifest.template.json") -Force
  # RuntimeHost owns its executable, configuration, and JSON protocol dependency.
  Copy-Item .\Maxwell.RuntimeHost\bin\Release\net48\* $runtimeDir -Recurse -Force

  if ($browserMode -eq "LocalOnly") {
    $bundledChromePath = Join-Path $runtimeDir "chrome-portable"
    if (Test-Path -LiteralPath $bundledChromePath -PathType Container) {
      Remove-Item -LiteralPath $bundledChromePath -Recurse -Force
    }
  }

  # This distribution is explicitly win-x64. Keep the source staging tree
  # complete for development, but do not ship native binaries that Windows x64
  # can never load. This saves roughly 75 MB without changing workflow support.
  $excludedRuntimePaths = @(
    (Join-Path $runtimeDir "x86"),
    (Join-Path $runtimeDir "grpc_csharp_ext.x86.dll"),
    (Join-Path $runtimeDir "grpc_csharp_ext.x64.dylib"),
    (Join-Path $runtimeDir "libgrpc_csharp_ext.x64.dylib"),
    (Join-Path $runtimeDir "libgrpc_csharp_ext.x64.so")
  )
  foreach ($excludedRuntimePath in $excludedRuntimePaths) {
    if (Test-Path -LiteralPath $excludedRuntimePath) {
      Remove-Item -LiteralPath $excludedRuntimePath -Recurse -Force
    }
  }

  # Symbols are useful when developing OpenRPA, but RuntimeHost does not need
  # them to execute workflows.
  Get-ChildItem -LiteralPath $runtimeDir -Filter "*.pdb" -File -Recurse |
    Remove-Item -Force

  # This company build uses Simplified Chinese (with English fallback). Remove
  # satellite resource folders for languages that cannot be selected by the UI.
  $excludedCultures = @(
    "cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "zh-Hant"
  )
  foreach ($culture in $excludedCultures) {
    $culturePath = Join-Path $runtimeDir $culture
    if (Test-Path -LiteralPath $culturePath -PathType Container) {
      Remove-Item -LiteralPath $culturePath -Recurse -Force
    }
  }

  if ($browserMode -ne "LocalOnly") {
    $bundledChromeRoot = Join-Path $runtimeDir "chrome-portable"
    $bundledChromeData = Join-Path $bundledChromeRoot "Data"

    # The seed profile only needs the official extension, its preferences and
    # extension storage. Chrome recreates these caches/models on demand in the
    # per-user copy, so shipping them only makes the network package larger.
    $browserCachePaths = @(
      (Join-Path $bundledChromeRoot "Cache"),
      (Join-Path $bundledChromeData "optimization_guide_model_store"),
      (Join-Path $bundledChromeData "GrShaderCache"),
      (Join-Path $bundledChromeData "BrowserMetrics-spare.pma"),
      (Join-Path $bundledChromeData "component_crx_cache"),
      (Join-Path $bundledChromeData "Crashpad"),
      (Join-Path $bundledChromeData "OptimizationHints"),
      (Join-Path $bundledChromeData "ShaderCache"),
      (Join-Path $bundledChromeData "GPUPersistentCache"),
      (Join-Path $bundledChromeData "Default\GPUCache"),
      (Join-Path $bundledChromeData "Default\DawnWebGPUCache"),
      (Join-Path $bundledChromeData "Default\DawnGraphiteCache")
    )
    foreach ($browserCachePath in $browserCachePaths) {
      if (Test-Path -LiteralPath $browserCachePath) {
        Remove-Item -LiteralPath $browserCachePath -Recurse -Force
      }
    }

    # Portable Chrome contains roughly 43 MB of locale packs. Maxwell is
    # deployed in Chinese, so retain Chinese plus English fallback only.
    Get-ChildItem -LiteralPath (Join-Path $bundledChromeRoot "Chrome") -Directory |
      ForEach-Object {
        $localeDirectory = Join-Path $_.FullName "Locales"
        if (Test-Path -LiteralPath $localeDirectory -PathType Container) {
          Get-ChildItem -LiteralPath $localeDirectory -File |
            Where-Object { $_.Name -notin @("zh-CN.pak", "en-US.pak") } |
            Remove-Item -Force
        }
      }
  }
} else {
  Write-Warning "runtime-staging was not found. The package will run WWF built-in activities only."
  Write-Warning "Run ..\scripts\build-openrpa-runtime.ps1 on Windows before publishing OpenRPA activity support."
}

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
$localBrowserExtensionInstaller = Join-Path $publishDir "install-openrpa-extension.ps1"
$browserModeConfig = Join-Path $publishDir "browser-mode.json"
$browserManifestTemplate = Join-Path $runtimeDir "chromemanifest.template.json"
@{ BrowserMode = $browserMode } | ConvertTo-Json | Set-Content -Path $browserModeConfig -Encoding UTF8
$bundledBrowserProfileManifest = Join-Path $runtimeDir "chrome-portable\Data\Default\Extensions\hpnihnhlcnfejboocnckgchjdofeaphe\1.0.0.6_0\manifest.json"
$requiredFiles = @($appExe, $runtimeExe, $runtimeConfig, $runtimeJson, $browserInstaller, $localBrowserExtensionInstaller, $browserManifestTemplate, $browserModeConfig)
if ($browserMode -ne "LocalOnly") {
  $requiredFiles += $bundledBrowserProfileManifest
}
foreach ($requiredFile in $requiredFiles) {
  if ([string]::IsNullOrWhiteSpace($requiredFile) -or -not (Test-Path $requiredFile -PathType Leaf)) {
    throw "Missing published file: $requiredFile"
  }
}

$zipFile = switch ($Mode) {
  "Bundled" { ".\publish\Maxwell-version4-bundled.zip" }
  "LocalBrowser" { ".\publish\Maxwell-version4-local-browser.zip" }
  "Shared" { ".\publish\Maxwell-shared-direct.zip" }
  "Standalone" { ".\publish\Maxwell-version4.zip" }
  default { ".\publish\Maxwell-win-x64-slim.zip" }
}
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipFile -Force

Write-Host ""
Write-Host "Published app: $appExe"
Write-Host "Maxwell RuntimeHost: $runtimeExe"
Write-Host "Browser Native Messaging setup: $browserInstaller"
if ($browserMode -ne "LocalOnly") {
  Write-Host "Bundled OpenRPA browser profile: $bundledBrowserProfileManifest"
} else {
  Write-Host "OpenRPA extension installer for local Chrome: $localBrowserExtensionInstaller"
}
Write-Host "Distribution package: $projectDir\$zipFile"
if ($Mode -eq "Slim") {
  Write-Host "The slim package requires .NET 8 Desktop Runtime."
}
