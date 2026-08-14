param(
  [int]$Iterations = 10,
  [string]$WorkflowRoot
)

$ErrorActionPreference = "Stop"

# Keep this script compatible with Windows PowerShell 5.1 even when the
# package is unpacked on a machine whose default script encoding is ANSI.
# The Chinese share path is therefore constructed from Unicode code points.
if ([string]::IsNullOrWhiteSpace($WorkflowRoot)) {
  $share = '0-' + (-join [char[]](0x5171,0x4EAB,0x6587,0x4EF6,0x5939))
  $department = '5-' + (-join [char[]](0x7269,0x6D41,0x79D1))
  $rpaFolder = 'RPA(' + (-join [char[]](0x52FF,0x5220)) + ')'
  $projectFolder = (-join [char[]](0x9879,0x76EE))
  $projectName = (-join [char[]](0x5B81,0x6CE2,0x6052,0x5E05)) + '2'
  $WorkflowRoot = "\\new-server\$share\$department\$rpaFolder\$projectFolder\$projectName"
}
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtime = Join-Path $packageRoot "runtime"
$runtimeHost = Join-Path $runtime "Maxwell.RuntimeHost.exe"
$chrome = Join-Path $runtime "chrome-portable\Chrome\chrome.exe"
$launcher = Join-Path $runtime "browser-launcher"
$workflow = Join-Path $WorkflowRoot "7d64c2ba-fb45-4e78-8584-d12bf1137b88.json"

foreach ($required in @($runtimeHost, $chrome, $workflow)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing required file: $required" }
}

$runId = "Maxwell-GetElementValidation-" + [Guid]::NewGuid().ToString("N")
$reportDirectory = Join-Path $env:TEMP $runId
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$profile = Join-Path $env:LOCALAPPDATA ("Maxwell\ValidationProfiles\" + $runId)

$oldEnvironment = @{}
foreach ($name in @("MAXWELL_BUNDLED_CHROME", "MAXWELL_CHROME_LAUNCHER", "MAXWELL_BROWSER_PROFILE", "MAXWELL_WINDOWS_UIA_COMPATIBILITY", "MAXWELL_BROWSER_DIAGNOSTICS", "MAXWELL_NOTIFICATION_HOST", "PATH")) {
  $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

function Stop-ProfileChrome {
  param([string]$TargetProfile)
  Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*$TargetProfile*" } |
    ForEach-Object {
      Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  do {
    $left = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -like "*$TargetProfile*" })
    if ($left.Count -eq 0) { return }
    Start-Sleep -Milliseconds 200
  } while ([DateTime]::UtcNow -lt $deadline)
  throw "Chrome processes for the test profile did not exit."
}

try {
  [Environment]::SetEnvironmentVariable("MAXWELL_BUNDLED_CHROME", $chrome, "Process")
  [Environment]::SetEnvironmentVariable("MAXWELL_CHROME_LAUNCHER", (Join-Path $launcher "chrome.exe"), "Process")
  [Environment]::SetEnvironmentVariable("MAXWELL_WINDOWS_UIA_COMPATIBILITY", "1", "Process")
  [Environment]::SetEnvironmentVariable("MAXWELL_BROWSER_DIAGNOSTICS", $reportDirectory, "Process")
  [Environment]::SetEnvironmentVariable("MAXWELL_NOTIFICATION_HOST", (Join-Path $runtime "Maxwell.NotificationHost.exe"), "Process")
  [Environment]::SetEnvironmentVariable("PATH", "$launcher;$([IO.Path]::GetDirectoryName($chrome));$($oldEnvironment['PATH'])", "Process")

  $results = @()
  for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    Stop-ProfileChrome $profile
    if (Test-Path -LiteralPath $profile) { Remove-Item -LiteralPath $profile -Recurse -Force }
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    [Environment]::SetEnvironmentVariable("MAXWELL_BROWSER_PROFILE", $profile, "Process")

    $logPath = Join-Path $reportDirectory ("iteration-" + $iteration + ".log")
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $output = & $runtimeHost run $workflow --runtime-dir $runtime --workflow-root $WorkflowRoot 2>&1 | Tee-Object -LiteralPath $logPath
    $exitCode = $LASTEXITCODE
    $stopwatch.Stop()
    $text = ($output | Out-String)
    $results += [pscustomobject]@{
      Iteration = $iteration
      ExitCode = $exitCode
      ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
      AccountLocated = $text -match 'automation-id=account' -and $text -match 'MAXWELL_UIA result=found'
      PasswordLocated = $text -match 'automation-id=password' -and $text -match 'MAXWELL_UIA result=found'
      FailedLocating = $text -match 'Failed locating 1 item\(s\)'
      RebindProtected = $text -match 'second-pass-overwrite-prevented=true'
      Log = $logPath
    }
    Stop-ProfileChrome $profile
  }
  $csv = Join-Path $reportDirectory "summary.csv"
  $results | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8
  $pass = @($results | Where-Object { $_.ExitCode -eq 0 -and $_.AccountLocated -and $_.PasswordLocated -and -not $_.FailedLocating }).Count
  $results | Format-Table -AutoSize
  "Detailed report: $reportDirectory"
  if ($pass -eq $Iterations) { "PASS: $pass/$Iterations runs located account and password without selector failure."; exit 0 }
  "FAIL: only $pass/$Iterations runs met the selector acceptance criteria. Send $reportDirectory back for analysis."; exit 1
}
finally {
  try { Stop-ProfileChrome $profile } catch { }
  foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], "Process") }
}
