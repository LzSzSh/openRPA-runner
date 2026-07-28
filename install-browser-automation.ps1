<#
Registers the Native Messaging Host bundled with a Maxwell portable package.

This script deliberately writes only per-user (HKCU) registry keys, so it does
not require administrator rights and does not alter any machine-wide browser
settings.  It cannot install a browser extension: Chrome/Edge require that
extension to be installed by the browser/user.
#>
[CmdletBinding()]
param(
    [ValidateSet("Chrome", "Edge", "Both")]
    [string]$Browser = "Both"
)

$ErrorActionPreference = "Stop"

$packageDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeDirectory = Join-Path $packageDirectory "runtime"
$hostExecutable = Join-Path $runtimeDirectory "OpenRPA.NativeMessagingHost.exe"
$manifestPath = Join-Path $runtimeDirectory "chromemanifest.json"
$manifestTemplatePath = Join-Path $runtimeDirectory "chromemanifest.template.json"

if (-not (Test-Path $hostExecutable -PathType Leaf)) {
    throw "Native Messaging Host was not found: $hostExecutable`nPlease run this script from the top level of the complete Maxwell package."
}
if (-not (Test-Path $manifestTemplatePath -PathType Leaf)) {
    throw "Browser manifest template was not found: $manifestTemplatePath`nPlease recreate the Maxwell package with the latest publish script."
}

# Do not parse and serialize the manifest through Windows PowerShell 5.1. On a
# non-ASCII package path it can corrupt the final backslash in a JSON escape.
# Generate a fresh manifest from an untouched template instead.
$manifestTemplate = Get-Content -LiteralPath $manifestTemplatePath -Raw
$jsonHostExecutable = $hostExecutable.Replace("\", "\\")
$manifestContent = $manifestTemplate.Replace("REPLACEPATH\\OpenRPA.NativeMessagingHost.exe", $jsonHostExecutable)
if ($manifestContent -eq $manifestTemplate -or $manifestContent -notmatch '"path"\s*:\s*"[^"]+OpenRPA\.NativeMessagingHost\.exe"') {
    throw "Could not create a valid browser manifest from: $manifestTemplatePath"
}
[System.IO.File]::WriteAllText($manifestPath, $manifestContent, [System.Text.UTF8Encoding]::new($false))

$registrations = @()
if ($Browser -eq "Chrome" -or $Browser -eq "Both") {
    $registrations += "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.openrpa.msg"
}
if ($Browser -eq "Edge" -or $Browser -eq "Both") {
    $registrations += "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.openrpa.msg"
}

foreach ($registryPath in $registrations) {
    New-Item -Path $registryPath -Force | Out-Null
    Set-Item -LiteralPath $registryPath -Value $manifestPath
    Write-Host "Registered Native Messaging Host: $registryPath"
}

$knownExtensionIds = @(
    "cjjehhadngahcdkbkeopdlmjedddkedh",
    "meoobaegjobjgfnlfndegpnpdmonbnbe",
    "eglkkjllkdooicijpbolleoemogkagbp",
    "ijabkdeadobnodfdgilkjhbploikblbg",
    "cfhdojbkjhnklbpkdaibdccddilifddb",
    "hpnihnhlcnfejboocnckgchjdofeaphe",
    "ebbbjigjoglkolagcfjdnginmfknnmmg",
    "ennlpladclaaogmhlddghpneajafmgln",
    "fdpjjldghhdjaakadnkiepghjdfjllmg",
    "hkjbghcanbbkhldlfkddbiiooadknael",
    "igkhnjpllpckiodjdkoiailagikebloa",
    "bnmpdfndpadhamjmmkgkhgkmplfancbi"
)

$profiles = @()
if ($Browser -eq "Chrome" -or $Browser -eq "Both") {
    $profiles += Join-Path $env:LOCALAPPDATA "Google\Chrome\User Data"
}
if ($Browser -eq "Edge" -or $Browser -eq "Both") {
    $profiles += Join-Path $env:LOCALAPPDATA "Microsoft\Edge\User Data"
}

$foundExtensions = @()
foreach ($profileRoot in $profiles) {
    foreach ($extensionId in $knownExtensionIds) {
        $extensionDirectory = Join-Path $profileRoot "Default\Extensions\$extensionId"
        if (Test-Path $extensionDirectory -PathType Container) {
            $foundExtensions += $extensionDirectory
        }
    }
}

Write-Host ""
if ($foundExtensions.Count -gt 0) {
    Write-Host "Detected an OpenRPA browser extension in:"
    $foundExtensions | ForEach-Object { Write-Host "  $_" }
    Write-Host "Close every Chrome/Edge window, reopen the browser, then run the Maxwell browser workflow again."
} else {
    Write-Warning "No known OpenRPA browser extension was detected."
    Write-Host "Install and enable the official OpenRPA browser extension in the selected browser first."
    Write-Host "Then fully close and reopen the browser before running the workflow."
    Write-Host "For file:/// workflows, open the extension details page and enable 'Allow access to file URLs'."
}
