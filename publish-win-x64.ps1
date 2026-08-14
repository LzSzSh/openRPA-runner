param(
  [ValidateSet("Final", "Slim", "Standalone", "Bundled", "LocalBrowser", "Shared", "BrowserRepairTest", "BrowserRepair", "BrowserRepair2", "BrowserRepair3", "BrowserRepair4", "BrowserRepair5", "BrowserRepair6", "BrowserRepair7", "BrowserRepair8", "BrowserRepair9", "WindowsUiaRepair1", "WindowsUiaRepair2", "WindowsUiaRepair3")]
  [string]$Mode = "Bundled"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectDir

$publishDir = switch ($Mode) {
  "Final" { ".\\publish\\Maxwell办公助手-20260814" }
  "Bundled" { ".\publish\Maxwell-version4-bundled" }
  "BrowserRepairTest" { ".\publish\Maxwell浏览器连接修复测试版" }
  "BrowserRepair" { ".\publish\Maxwell浏览器连接修复版" }
  "BrowserRepair2" { ".\publish\Maxwell浏览器连接修复版2" }
  "BrowserRepair3" { ".\publish\Maxwell浏览器连接修复版3" }
  "BrowserRepair4" { ".\publish\Maxwell浏览器连接修复版4" }
  "BrowserRepair5" { ".\publish\Maxwell浏览器连接修复版5" }
  "BrowserRepair6" { ".\publish\Maxwell浏览器连接修复版6" }
  "BrowserRepair7" { ".\publish\Maxwell浏览器连接修复版7" }
  "BrowserRepair8" { ".\publish\Maxwell浏览器连接修复版8" }
  "BrowserRepair9" { ".\publish\Maxwell浏览器连接修复版9" }
  "WindowsUiaRepair1" { ".\publish\Maxwell-Windows定位修复版1" }
  "WindowsUiaRepair2" { ".\publish\Maxwell-Windows定位修复版2" }
  "WindowsUiaRepair3" { ".\publish\Maxwell-Windows定位修复版3" }
  "LocalBrowser" { ".\publish\Maxwell-version4-local-browser" }
  "Shared" { ".\publish\Maxwell-shared-direct" }
  "Standalone" { ".\publish\Maxwell-version4" }
  default { ".\publish\win-x64-slim" }
}
$selfContained = if ($Mode -eq "Slim") { "false" } else { "true" }
$browserMode = if ($Mode -eq "LocalBrowser") { "LocalOnly" } elseif ($Mode -eq "Final" -or $Mode -eq "Bundled" -or $Mode -like "BrowserRepair*" -or $Mode -eq "WindowsUiaRepair1" -or $Mode -eq "WindowsUiaRepair2" -or $Mode -eq "WindowsUiaRepair3") { "BundledOnly" } else { "Both" }
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
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

dotnet build .\Maxwell.RuntimeHost\Maxwell.RuntimeHost.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Maxwell.RuntimeHost build failed with exit code $LASTEXITCODE" }
dotnet build .\Maxwell.NotificationHost\Maxwell.NotificationHost.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Maxwell.NotificationHost build failed with exit code $LASTEXITCODE" }
dotnet build .\Maxwell.ChromeLauncher\Maxwell.ChromeLauncher.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Maxwell.ChromeLauncher build failed with exit code $LASTEXITCODE" }
$runtimeDir = Join-Path $publishDir "runtime"
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runtimeDir "browser-launcher") -Force | Out-Null
Copy-Item .\Maxwell.RuntimeHost\bin\Release\net48\* $runtimeDir -Recurse -Force
Copy-Item .\Maxwell.ChromeLauncher\bin\Release\net48\* (Join-Path $runtimeDir "browser-launcher") -Recurse -Force
Copy-Item .\install-browser-automation.ps1 $publishDir -Force
Copy-Item .\install-openrpa-extension.ps1 $publishDir -Force
    if ($Mode -eq "Final" -or $Mode -eq "WindowsUiaRepair1" -or $Mode -eq "WindowsUiaRepair2" -or $Mode -eq "WindowsUiaRepair3") {
  Copy-Item .\test-windows-uia-launch.ps1 $publishDir -Force
  if (Test-Path .\test-maxwell-login-stability.ps1) {
    Copy-Item .\test-maxwell-login-stability.ps1 $publishDir -Force
  }
}

$openRpaRuntimeStaging = ".\runtime-staging"
$baselineRuntime = ".\publish\Maxwell浏览器基线版\runtime"
$runtimeSource = if ($Mode -eq "WindowsUiaRepair1" -or $Mode -eq "WindowsUiaRepair2" -or $Mode -eq "WindowsUiaRepair3") { $baselineRuntime } else { $openRpaRuntimeStaging }
if (Test-Path $runtimeSource -PathType Container) {
  Copy-Item (Join-Path $runtimeSource "*") $runtimeDir -Recurse -Force
  if (Test-Path (Join-Path $runtimeSource "chromemanifest.json")) {
    Copy-Item (Join-Path $runtimeSource "chromemanifest.json") (Join-Path $runtimeDir "chromemanifest.template.json") -Force
  }
  # RuntimeHost owns its executable, configuration, and JSON protocol dependency.
  Copy-Item .\Maxwell.RuntimeHost\bin\Release\net48\* $runtimeDir -Recurse -Force
  Copy-Item .\Maxwell.NotificationHost\bin\Release\net48\* $runtimeDir -Recurse -Force
  Copy-Item .\Maxwell.ChromeLauncher\bin\Release\net48\* (Join-Path $runtimeDir "browser-launcher") -Recurse -Force

  # Maxwell always loads its private auto-reconnecting MV3 extension. Repair
  # modes use the previous baseline runtime as their source, so copy this
  # directory explicitly from staging and fail publication if it is incomplete.
  $maxwellExtensionSource = Join-Path $openRpaRuntimeStaging "maxwell-openrpa-extension"
  $maxwellExtensionTarget = Join-Path $runtimeDir "maxwell-openrpa-extension"
  foreach ($requiredExtensionFile in @("manifest.json", "background.js", "content.js")) {
    $requiredSourcePath = Join-Path $maxwellExtensionSource $requiredExtensionFile
    if (-not (Test-Path -LiteralPath $requiredSourcePath -PathType Leaf)) {
      throw "Missing Maxwell browser extension file: $requiredSourcePath"
    }
  }
  New-Item -ItemType Directory -Path $maxwellExtensionTarget -Force | Out-Null
  Copy-Item (Join-Path $maxwellExtensionSource "*") $maxwellExtensionTarget -Recurse -Force

  if ($Mode -eq "Final" -or $Mode -eq "WindowsUiaRepair1" -or $Mode -eq "WindowsUiaRepair2" -or $Mode -eq "WindowsUiaRepair3") {
    $windowsSelectorAssembly = Join-Path $projectDir "..\upstream\openrpa\dist\net462\OpenRPA.Windows.dll"
    $startProcessAssembly = Join-Path $projectDir "..\upstream\openrpa\dist\net462\OpenRPA.Utilities.dll"
    if (-not (Test-Path -LiteralPath $windowsSelectorAssembly -PathType Leaf)) {
      throw "Missing rebuilt OpenRPA.Windows.dll: $windowsSelectorAssembly"
    }
    Copy-Item $windowsSelectorAssembly (Join-Path $runtimeDir "OpenRPA.Windows.dll") -Force
    if (-not (Test-Path -LiteralPath $startProcessAssembly -PathType Leaf)) {
      throw "Missing rebuilt OpenRPA.Utilities.dll: $startProcessAssembly"
    }
    Copy-Item $startProcessAssembly (Join-Path $runtimeDir "OpenRPA.Utilities.dll") -Force
    $chromeIniPath = Join-Path $runtimeDir "chrome-portable\Chrome\chrome++.ini"
    if (-not (Test-Path -LiteralPath $chromeIniPath -PathType Leaf)) {
      throw "Missing Chrome++ configuration: $chromeIniPath"
    }
    $chromeIni = Get-Content -LiteralPath $chromeIniPath -Raw
    $chromeIni = [regex]::Replace($chromeIni, '(?m)^command_line=(.*)$', {
        param($match)
        if ($match.Groups[1].Value -match '--force-renderer-accessibility') { return $match.Value }
        return 'command_line=' + $match.Groups[1].Value.Trim() + ' --force-renderer-accessibility'
      }, 1)
    Set-Content -LiteralPath $chromeIniPath -Value $chromeIni -Encoding UTF8
  }

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

    # Never distribute browser history, credentials, cookies, sessions, form
    # data, or machine-specific state from the profile used to seed the
    # portable browser. Keep only the extension installation and preferences
    # required by Maxwell; Chrome recreates the removed files on first launch.
    $browserPrivateDataPaths = @(
      (Join-Path $bundledChromeRoot "Chrome\debug.log"),
      (Join-Path $bundledChromeData "Local State"),
      (Join-Path $bundledChromeData "Default\Account Web Data"),
      (Join-Path $bundledChromeData "Default\Account Web Data-journal"),
      (Join-Path $bundledChromeData "Default\Cookies"),
      (Join-Path $bundledChromeData "Default\Cookies-journal"),
      (Join-Path $bundledChromeData "Default\Favicons"),
      (Join-Path $bundledChromeData "Default\Favicons-journal"),
      (Join-Path $bundledChromeData "Default\History"),
      (Join-Path $bundledChromeData "Default\History-journal"),
      (Join-Path $bundledChromeData "Default\Login Data"),
      (Join-Path $bundledChromeData "Default\Login Data-journal"),
      (Join-Path $bundledChromeData "Default\Login Data For Account"),
      (Join-Path $bundledChromeData "Default\Login Data For Account-journal"),
      (Join-Path $bundledChromeData "Default\Network\Cookies"),
      (Join-Path $bundledChromeData "Default\Network\Cookies-journal"),
      (Join-Path $bundledChromeData "Default\Network\TransportSecurity"),
      (Join-Path $bundledChromeData "Default\Sessions"),
      (Join-Path $bundledChromeData "Default\Shortcuts"),
      (Join-Path $bundledChromeData "Default\Shortcuts-journal"),
      (Join-Path $bundledChromeData "Default\Top Sites"),
      (Join-Path $bundledChromeData "Default\Top Sites-journal"),
      (Join-Path $bundledChromeData "Default\Visited Links"),
      (Join-Path $bundledChromeData "Default\Web Data"),
      (Join-Path $bundledChromeData "Default\Web Data-journal")
    )
    foreach ($browserPrivateDataPath in $browserPrivateDataPaths) {
      if (Test-Path -LiteralPath $browserPrivateDataPath) {
        Remove-Item -LiteralPath $browserPrivateDataPath -Recurse -Force
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
  Write-Warning "The selected runtime source was not found. The package will run WWF built-in activities only."
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
$notificationHost = Join-Path $runtimeDir "Maxwell.NotificationHost.exe"
$runtimeJson = Join-Path $runtimeDir "Newtonsoft.Json.dll"
$browserInstaller = Join-Path $publishDir "install-browser-automation.ps1"
$localBrowserExtensionInstaller = Join-Path $publishDir "install-openrpa-extension.ps1"
$browserModeConfig = Join-Path $publishDir "browser-mode.json"
$browserManifestTemplate = Join-Path $runtimeDir "chromemanifest.template.json"
$uiaLaunchTest = Join-Path $publishDir "test-windows-uia-launch.ps1"
@{ BrowserMode = $browserMode } | ConvertTo-Json | Set-Content -Path $browserModeConfig -Encoding UTF8
if ($Mode -eq "BrowserRepairTest") {
  @"
Maxwell 浏览器连接修复测试版

本版保留浏览器连接修复能力，并新增定时任务：
1. 可按项目名创建、查询和删除定时任务。
2. 支持每周指定星期或每月指定日期，并精确到时、分执行。
3. 到期后固定执行所选项目按名称排序的第一个工作流。
4. 定时任务需要执行器保持打开并处于运行模式。
"@ | Set-Content -Path (Join-Path $publishDir "修复说明.txt") -Encoding UTF8
}
if ($Mode -eq "Final") {
  @"
Maxwell 麦威数字助手正式交接版（2026-08-14）

本版冻结了内置浏览器、浏览器 Native Messaging、Windows UI Automation、
共享工作流库、运行/编辑模式、定时任务和独立 RuntimeHost 等最终功能。

源码、构建方法、已知限制和 GitHub 版本信息见随交付包提供的《Maxwell最终交接文档.md》。
"@ | Set-Content -Path (Join-Path $publishDir "版本说明.txt") -Encoding UTF8
}
$bundledBrowserProfileManifest = Join-Path $runtimeDir "chrome-portable\Data\Default\Extensions\hpnihnhlcnfejboocnckgchjdofeaphe\1.0.0.6_0\manifest.json"
$requiredFiles = @($appExe, $runtimeExe, $runtimeConfig, $notificationHost, $runtimeJson, $browserInstaller, $localBrowserExtensionInstaller, $browserManifestTemplate, $browserModeConfig)
if ($Mode -eq "Final" -or $Mode -eq "WindowsUiaRepair1" -or $Mode -eq "WindowsUiaRepair2" -or $Mode -eq "WindowsUiaRepair3") {
  $requiredFiles += $uiaLaunchTest
  $requiredFiles += (Join-Path $publishDir "test-maxwell-login-stability.ps1")
}
if ($browserMode -ne "LocalOnly") {
  $requiredFiles += $bundledBrowserProfileManifest
  $bundledChromeRoot = Join-Path $runtimeDir "chrome-portable"
  $bundledChromeExe = Join-Path $bundledChromeRoot "Chrome\chrome.exe"
  $bundledChromeDll = Get-ChildItem -LiteralPath $bundledChromeRoot -Filter "chrome.dll" -File -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not (Test-Path -LiteralPath $bundledChromeExe -PathType Leaf) -or $null -eq $bundledChromeDll) {
    throw "Bundled Chromium runtime is incomplete. Expected Chrome\\chrome.exe and a matching chrome.dll below $bundledChromeRoot"
  }
}
foreach ($requiredFile in $requiredFiles) {
  if ([string]::IsNullOrWhiteSpace($requiredFile) -or -not (Test-Path $requiredFile -PathType Leaf)) {
    throw "Missing published file: $requiredFile"
  }
}

$zipFile = switch ($Mode) {
  "Final" { ".\\publish\\Maxwell办公助手-20260814.zip" }
  "Bundled" { ".\publish\Maxwell-version4-bundled.zip" }
  "BrowserRepairTest" { ".\publish\Maxwell浏览器连接修复测试版.zip" }
  "BrowserRepair" { ".\publish\Maxwell浏览器连接修复版.zip" }
  "BrowserRepair2" { ".\publish\Maxwell浏览器连接修复版2.zip" }
  "BrowserRepair3" { ".\publish\Maxwell浏览器连接修复版3.zip" }
  "BrowserRepair4" { ".\publish\Maxwell浏览器连接修复版4.zip" }
  "BrowserRepair5" { ".\publish\Maxwell浏览器连接修复版5.zip" }
  "BrowserRepair6" { ".\publish\Maxwell浏览器连接修复版6.zip" }
  "BrowserRepair7" { ".\publish\Maxwell浏览器连接修复版7.zip" }
  "BrowserRepair8" { ".\publish\Maxwell浏览器连接修复版8.zip" }
  "BrowserRepair9" { ".\publish\Maxwell浏览器连接修复版9.zip" }
  "WindowsUiaRepair1" { ".\publish\Maxwell-Windows定位修复版1.zip" }
  "WindowsUiaRepair2" { ".\publish\Maxwell-Windows定位修复版2.zip" }
  "WindowsUiaRepair3" { ".\publish\Maxwell-Windows定位修复版3.zip" }
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

