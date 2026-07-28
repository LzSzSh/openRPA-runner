using System.Diagnostics;
using System.IO;

namespace OpenRpaWorkflowLauncher.Services;

/// <summary>
/// Starts the bundled Chromium-family browser in a Maxwell-owned profile and
/// loads the Maxwell MV3 bridge from the package. The user’s normal Chrome
/// profile and extensions are never used or modified.
/// </summary>
public sealed class MaxwellBrowserBootstrapper
{
    private readonly ChromeAutomationStatusService _nativeHostService = new();

    public string LaunchExtensionManagementPage()
    {
        return Launch("chrome://extensions/");
    }

    public string Launch(string targetUrl)
    {
        string? executable = BundledChromeLocator.FindBundledChromeExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("未找到 Maxwell 内置浏览器 chrome.exe。请确认使用的是完整发布包。", executable);
        }

        string? extensionDirectory = BundledChromeLocator.FindBundledExtensionDirectory();
        if (string.IsNullOrWhiteSpace(extensionDirectory))
        {
            throw new DirectoryNotFoundException("未找到 Maxwell 内置浏览器扩展。请确认 runtime\\browser-extension\\manifest.json 存在。");
        }

        _nativeHostService.RegisterBundledNativeHost();
        Directory.CreateDirectory(BundledChromeLocator.MaxwellBrowserProfileDirectory);

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = string.Join(' ',
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-default-apps",
                "--allow-file-access-from-files",
                QuoteArgument("--user-data-dir=" + BundledChromeLocator.MaxwellBrowserProfileDirectory),
                QuoteArgument("--load-extension=" + extensionDirectory),
                QuoteArgument(targetUrl)),
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)
        });

        return executable;
    }

    private static string QuoteArgument(string value) => '"' + value.Replace("\"", "\\\"") + '"';
}
