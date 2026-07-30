<#
Opens the official OpenRPA extension page for the LocalBrowser Maxwell package.

Chrome does not permit an ordinary desktop application to silently install an
extension. The user confirms the official extension installation once in Chrome.
This script also registers Maxwell's Native Messaging Host for the current user.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$packageDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$nativeHostSetup = Join-Path $packageDirectory "install-browser-automation.ps1"
if (-not (Test-Path -LiteralPath $nativeHostSetup -PathType Leaf)) {
    throw "Native Messaging Host setup script was not found: $nativeHostSetup"
}

& $nativeHostSetup -Browser Chrome

$extensionUrl = "https://chromewebstore.google.com/detail/openrpa/hpnihnhlcnfejboocnckgchjdofeaphe"
Start-Process $extensionUrl

Write-Host ""
Write-Host "The official OpenRPA Chrome extension page is open. Click 'Add to Chrome' and confirm installation."
Write-Host "After installation, close Chrome completely, reopen it, and then run the Maxwell browser workflow."
