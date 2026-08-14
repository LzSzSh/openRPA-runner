param(
  [string]$Url = "https://hmc.ccsrm.com",
  [ValidateRange(1, 20)]
  [int]$Iterations = 10
)

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeRoot = Join-Path $packageRoot "runtime"
$launcher = Join-Path $runtimeRoot "browser-launcher\chrome.exe"
$chromeSourceRoot = Join-Path $runtimeRoot "chrome-portable\Chrome"
$chromeSource = Join-Path $chromeSourceRoot "chrome.exe"
$validationRoot = Join-Path $env:LOCALAPPDATA "Maxwell\UiaValidationChrome"
$validationChrome = Join-Path $validationRoot "chrome.exe"
$summaryDirectory = Join-Path $env:TEMP ("Maxwell-UiaValidation-" + [guid]::NewGuid().ToString("N"))

foreach ($required in @($launcher, $chromeSource)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
    throw "Missing required file: $required"
  }
}

function Get-FileFingerprint([string]$Path) {
  $item = Get-Item -LiteralPath $Path
  return "{0}|{1}|{2}" -f $Path, $item.Length, $item.LastWriteTimeUtc.Ticks
}

function Ensure-LocalValidationChrome {
  $fingerprint = Get-FileFingerprint $chromeSource
  $stateFile = Join-Path $validationRoot "validation-source.txt"
  if ((Test-Path -LiteralPath $validationChrome -PathType Leaf) -and
      (Test-Path -LiteralPath $stateFile -PathType Leaf) -and
      ((Get-Content -LiteralPath $stateFile -Raw).Trim() -eq $fingerprint)) {
    return
  }

  $parent = Split-Path -Parent $validationRoot
  New-Item -ItemType Directory -Path $parent -Force | Out-Null
  $staging = Join-Path $parent (".UiaValidationChrome-" + [guid]::NewGuid().ToString("N"))
  try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Copy-Item -Path (Join-Path $chromeSourceRoot "*") -Destination $staging -Recurse -Force
    Set-Content -LiteralPath (Join-Path $staging "validation-source.txt") -Value $fingerprint -Encoding UTF8
    if (Test-Path -LiteralPath $validationRoot) {
      Remove-Item -LiteralPath $validationRoot -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $validationRoot
  }
  finally {
    if (Test-Path -LiteralPath $staging) {
      Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
  }
}

function Get-ProfileChromeProcesses([string]$Profile) {
  return @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($Profile, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Get-BrowserMainProcess([string]$Profile, [string]$ChromePath) {
  $normalizedChrome = [IO.Path]::GetFullPath($ChromePath)
  $candidates = foreach ($item in Get-ProfileChromeProcesses $Profile) {
    if (-not $item.ExecutablePath -or -not $item.CommandLine) { continue }
    try {
      if (-not [string]::Equals([IO.Path]::GetFullPath($item.ExecutablePath), $normalizedChrome, [StringComparison]::OrdinalIgnoreCase)) { continue }
    }
    catch { continue }
    if ($item.CommandLine -match '(?i)--type(?:=|\s)') { continue }
    try {
      $process = Get-Process -Id $item.ProcessId -ErrorAction Stop
      $handle = $process.MainWindowHandle
      [pscustomobject]@{
        ProcessId = [int]$item.ProcessId
        MainWindowHandle = [Int64]$handle
        CommandLine = $item.CommandLine
      }
    }
    catch {
      # The launcher records the corresponding process-race entry. A vanished
      # candidate is not a browser-launch failure by itself.
    }
  }
  return @($candidates | Sort-Object @{ Expression = { $_.MainWindowHandle -ne 0 }; Descending = $true }, ProcessId | Select-Object -First 1)
}

function Stop-ProfileChromeProcesses([string]$Profile) {
  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  do {
    $processes = Get-ProfileChromeProcesses $Profile
    foreach ($item in $processes) {
      Stop-Process -Id $item.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($processes.Count -eq 0) { return $true }
    Start-Sleep -Milliseconds 150
  } while ([DateTime]::UtcNow -lt $deadline)
  return ((Get-ProfileChromeProcesses $Profile).Count -eq 0)
}

Ensure-LocalValidationChrome
New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
Write-Host "Validation Chrome: $validationChrome"
Write-Host "Validation summaries: $summaryDirectory"

$oldEnvironment = @{}
foreach ($name in @("MAXWELL_BUNDLED_CHROME", "MAXWELL_BROWSER_PROFILE", "MAXWELL_BROWSER_DIAGNOSTICS")) {
  $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

$results = @()
try {
  for ($index = 1; $index -le $Iterations; $index++) {
    $testId = [guid]::NewGuid().ToString("N")
    $profile = Join-Path $env:TEMP "Maxwell-UiaProbe-$testId"
    $diagnostics = Join-Path $env:TEMP "Maxwell-UiaDiagnostics-$testId"
    New-Item -ItemType Directory -Path $profile, $diagnostics -Force | Out-Null
    $cleanupCompleted = $false

    try {
      if ((Get-ProfileChromeProcesses $profile).Count -ne 0) {
        throw "Unexpected residual Chrome process for the new test profile: $profile"
      }

      $env:MAXWELL_BUNDLED_CHROME = $validationChrome
      $env:MAXWELL_BROWSER_PROFILE = $profile
      $env:MAXWELL_BROWSER_DIAGNOSTICS = $diagnostics
      & $launcher $Url
      $exitCode = $LASTEXITCODE

      $launchLog = Get-ChildItem -LiteralPath $diagnostics -Filter "chrome-uia-launch-*.log" -File |
        Sort-Object LastWriteTime | Select-Object -Last 1
      $logText = if ($null -ne $launchLog) { Get-Content -LiteralPath $launchLog.FullName -Raw } else { "" }
      $browserMain = @(Get-BrowserMainProcess $profile $validationChrome | Select-Object -First 1)[0]
      $windowMatch = [regex]::Match($logText, 'main-window pid=(\d+); handle=0x([0-9A-F]+); elapsed-ms=(\d+)')
      $documentMatch = [regex]::Match($logText, 'uia-document pid=(\d+); elapsed-ms=(\d+)')
      $uiaErrorMatch = [regex]::Match($logText, 'last-uia-hresult=0x([0-9A-F]+)')
      $raceMatch = [regex]::Match($logText, 'process-races=(\d+)')
      $ready = $logText -match 'result=ready'
      $forceAccessibility = $null -ne $browserMain -and $browserMain.CommandLine -match '--force-renderer-accessibility'

      $results += [pscustomobject]@{
        Iteration = $index
        LauncherExitCode = $exitCode
        BrowserMainPid = if ($null -ne $browserMain) { $browserMain.ProcessId } else { $null }
        BrowserMainWindowHandle = if ($null -ne $browserMain) { ('0x{0:X}' -f $browserMain.MainWindowHandle) } else { $null }
        MainProcessCommandLine = if ($null -ne $browserMain) { $browserMain.CommandLine } else { $null }
        MatchingChromeProcesses = (Get-ProfileChromeProcesses $profile).Count
        WindowReadyMilliseconds = if ($windowMatch.Success) { [int]$windowMatch.Groups[3].Value } else { $null }
        DocumentReadyMilliseconds = if ($documentMatch.Success) { [int]$documentMatch.Groups[2].Value } else { $null }
        UiAutomationReady = $ready
        ForceAccessibilityInCommandLine = $forceAccessibility
        ProcessRaceCount = if ($raceMatch.Success) { [int]$raceMatch.Groups[1].Value } else { 0 }
        LastUiaExceptionHResult = if ($uiaErrorMatch.Success) { '0x' + $uiaErrorMatch.Groups[1].Value } else { $null }
        Passed = ($exitCode -eq 0 -and $ready -and $forceAccessibility -and $null -ne $browserMain -and $browserMain.MainWindowHandle -ne 0)
        DiagnosticLog = if ($null -ne $launchLog) { $launchLog.FullName } else { "<missing>" }
      }
    }
    finally {
      $cleanupCompleted = Stop-ProfileChromeProcesses $profile
      if ($cleanupCompleted) {
        Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
      }
    }
  }
}
finally {
  foreach ($name in $oldEnvironment.Keys) {
    [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], "Process")
  }
}

$summaryFile = Join-Path $summaryDirectory "uia-cold-start-results.json"
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryFile -Encoding UTF8
$results | Select-Object Iteration, LauncherExitCode, BrowserMainPid, BrowserMainWindowHandle, MatchingChromeProcesses, WindowReadyMilliseconds, DocumentReadyMilliseconds, UiAutomationReady, ForceAccessibilityInCommandLine, ProcessRaceCount, LastUiaExceptionHResult, Passed, DiagnosticLog | Format-Table -AutoSize
Write-Host "Detailed result file: $summaryFile"

if (($results | Where-Object { -not $_.Passed }).Count -gt 0) {
  throw "UIA cold-start validation failed. Review the DiagnosticLog paths and $summaryFile."
}

Write-Host "PASS: $Iterations/$Iterations local-disk cold starts produced a Chrome UIA Document and carried --force-renderer-accessibility."
