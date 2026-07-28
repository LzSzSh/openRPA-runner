using System;
using System.IO;
using System.Linq;

namespace OpenRpaWorkflowLauncher.Services;

public static class BundledChromeLocator
{
    public static string MaxwellBrowserProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Maxwell",
        "BrowserProfile");

    public static string? FindBundledExtensionDirectory()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "runtime", "browser-extension"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "BrowserExtension"))
        ];

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "manifest.json")));
    }

    public static string? FindBundledChromeExecutable()
    {
        string[] directCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome-portable", "chrome.exe"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome", "chrome.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome-portable", "chrome.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome", "chrome.exe"))
        ];

        string? direct = directCandidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        string[] searchRoots =
        [
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome-portable"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome-portable")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome"))
        ];

        foreach (string root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;
            string? nested = Directory.EnumerateFiles(root, "chrome.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(nested)) return nested;
        }

        return null;
    }

    public static string? FindBundledChromeDirectory()
    {
        string? executable = FindBundledChromeExecutable();
        return executable is null ? null : Path.GetDirectoryName(executable);
    }

    public static string? FindBundledChromeProfileRoot()
    {
        string? executable = FindBundledChromeExecutable();
        if (executable is null) return null;

        string browserRoot = Path.GetDirectoryName(executable)!;
        string[] candidates =
        [
            Path.Combine(browserRoot, "User Data"),
            Path.Combine(browserRoot, "Data", "User Data"),
            Path.Combine(browserRoot, "profile"),
            Path.Combine(browserRoot, "Profile")
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }
}
