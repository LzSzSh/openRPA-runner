using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenRpaWorkflowLauncher.Services;

public static class BundledChromeLocator
{
    private const string OpenRpaExtensionId = "hpnihnhlcnfejboocnckgchjdofeaphe";
    private const string LocalChromeFolderName = "BundledChrome";
    private const string LocalNativeHostFolderName = "NativeMessagingHost";
    private const string SourceStateFilename = ".maxwell-source.json";

    public static string MaxwellBrowserProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Maxwell",
        "BrowserProfile");

    public static string? EnsureLocalNativeMessagingHostDirectory()
    {
        string? sourceRuntime = FindBundledRuntimeDirectory();
        if (string.IsNullOrWhiteSpace(sourceRuntime)) return null;

        string sourceHost = Path.Combine(sourceRuntime, "OpenRPA.NativeMessagingHost.exe");
        if (!File.Exists(sourceHost)) return null;

        string localDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maxwell", LocalNativeHostFolderName);
        if (IsLocalCopyCurrent(localDirectory, Path.Combine(localDirectory, "OpenRPA.NativeMessagingHost.exe"), sourceRuntime, new FileInfo(sourceHost))) return localDirectory;

        string parent = Path.GetDirectoryName(localDirectory)!;
        string staging = Path.Combine(parent, $".{LocalNativeHostFolderName}.sync-{Guid.NewGuid():N}");
        string backup = Path.Combine(parent, $".{LocalNativeHostFolderName}.backup-{Guid.NewGuid():N}");
        bool promoted = false;
        string[] requiredFiles =
        [
            "OpenRPA.NativeMessagingHost.exe", "OpenRPA.NativeMessagingHost.exe.config",
            "OpenRPA.Interfaces.dll", "OpenRPA.NamedPipeWrapper.dll", "Newtonsoft.Json.dll",
            "FlaUI.Core.dll", "FlaUI.UIA3.dll", "NLog.dll", "chromemanifest.template.json"
        ];

        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(staging);
            foreach (string file in requiredFiles)
            {
                string source = Path.Combine(sourceRuntime, file);
                if (File.Exists(source)) File.Copy(source, Path.Combine(staging, file), overwrite: true);
            }
            File.WriteAllText(Path.Combine(staging, SourceStateFilename), JsonSerializer.Serialize(new ChromeSourceState
            {
                SourceDirectory = sourceRuntime,
                ChromeLength = new FileInfo(sourceHost).Length,
                ChromeLastWriteTimeUtc = new FileInfo(sourceHost).LastWriteTimeUtc
            }));
            if (Directory.Exists(localDirectory))
            {
                try
                {
                    Directory.Move(localDirectory, backup);
                }
                catch (IOException) when (File.Exists(Path.Combine(localDirectory, "OpenRPA.NativeMessagingHost.exe")))
                {
                    // A previous browser session can keep the native host open.
                    // It is safer to keep using that complete local copy than to
                    // fail an otherwise runnable workflow just because it cannot
                    // be refreshed at this instant.
                    return localDirectory;
                }
            }
            Directory.Move(staging, localDirectory);
            promoted = true;
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            return localDirectory;
        }
        catch (Exception ex)
        {
            if (!Directory.Exists(localDirectory) && Directory.Exists(backup))
            {
                try { Directory.Move(backup, localDirectory); } catch { }
            }
            throw new InvalidOperationException("无法准备 Maxwell 本机 Native Messaging Host。", ex);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (promoted && Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
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

    /// <summary>
    /// Chrome's sandboxed child processes are unreliable when their executable
    /// tree resides on an SMB/UNC share. Keep Maxwell's bundled browser, but
    /// mirror it once per Windows user before launching it.
    /// </summary>
    public static string? EnsureLocalBundledChromeDirectory()
    {
        string? sourceDirectory = FindBundledChromeDirectory();
        if (string.IsNullOrWhiteSpace(sourceDirectory)) return null;
        if (!IsNetworkLocation(sourceDirectory)) return sourceDirectory;

        string sourceExecutable = Path.Combine(sourceDirectory, "chrome.exe");
        if (!File.Exists(sourceExecutable)) return null;

        FileInfo sourceInfo = new(sourceExecutable);
        string localDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Maxwell",
            LocalChromeFolderName);
        string localExecutable = Path.Combine(localDirectory, "chrome.exe");
        if (IsLocalCopyCurrent(localDirectory, localExecutable, sourceDirectory, sourceInfo))
        {
            return localDirectory;
        }

        string parent = Path.GetDirectoryName(localDirectory)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".{LocalChromeFolderName}.sync-{Guid.NewGuid():N}");
        string backup = Path.Combine(parent, $".{LocalChromeFolderName}.backup-{Guid.NewGuid():N}");
        bool localCopyPromoted = false;

        try
        {
            CopyDirectory(sourceDirectory, staging);
            File.WriteAllText(
                Path.Combine(staging, SourceStateFilename),
                JsonSerializer.Serialize(new ChromeSourceState
                {
                    SourceDirectory = sourceDirectory,
                    ChromeLength = sourceInfo.Length,
                    ChromeLastWriteTimeUtc = sourceInfo.LastWriteTimeUtc
                }));

            if (Directory.Exists(localDirectory))
            {
                try
                {
                    Directory.Move(localDirectory, backup);
                }
                catch (IOException) when (File.Exists(localExecutable))
                {
                    // Chrome can keep files locked after a workflow ends. A
                    // known-good local browser is safer than falling back to UNC.
                    return localDirectory;
                }
            }

            Directory.Move(staging, localDirectory);
            localCopyPromoted = true;
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            return localDirectory;
        }
        catch (Exception ex)
        {
            if (!Directory.Exists(localDirectory) && Directory.Exists(backup))
            {
                try { Directory.Move(backup, localDirectory); }
                catch { /* Preserve the backup folder rather than deleting it. */ }
            }

            throw new InvalidOperationException(
                "无法准备 Maxwell 本机内置浏览器。请确认本机磁盘空间充足，并且共享盘中的 chrome-portable 文件夹可读取。",
                ex);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (localCopyPromoted && Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
    }

    public static string? FindBundledChromeProfileRoot()
    {
        string? executable = FindBundledChromeExecutable();
        if (executable is null) return null;

        string browserRoot = Path.GetDirectoryName(executable)!;
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(browserRoot, "..", "Data")),
            Path.Combine(browserRoot, "User Data"),
            Path.Combine(browserRoot, "Data", "User Data"),
            Path.Combine(browserRoot, "profile"),
            Path.Combine(browserRoot, "Profile")
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Materialises the clean portable Chrome profile that already contains the
    /// Web Store-installed OpenRPA extension. Existing Maxwell profiles created
    /// by older builds are replaced only when they do not contain that extension.
    /// Once initialised, each Windows user keeps an independent local profile.
    /// </summary>
    public static string EnsureLocalBundledBrowserProfileDirectory()
    {
        string? sourceDirectory = FindBundledChromeProfileRoot();
        if (string.IsNullOrWhiteSpace(sourceDirectory) ||
            !ContainsOpenRpaExtension(sourceDirectory))
        {
            throw new InvalidOperationException(
                $"Maxwell 内置浏览器配置不完整，未找到 OpenRPA 扩展（{OpenRpaExtensionId}）。请重新生成完整发布包。");
        }

        string localDirectory = MaxwellBrowserProfileDirectory;
        if (ContainsOpenRpaExtension(localDirectory)) return localDirectory;

        string parent = Path.GetDirectoryName(localDirectory)!;
        string staging = Path.Combine(parent, $".BrowserProfile.sync-{Guid.NewGuid():N}");
        string backup = Path.Combine(parent, $".BrowserProfile.backup-{Guid.NewGuid():N}");
        bool promoted = false;

        try
        {
            Directory.CreateDirectory(parent);
            CopyDirectory(sourceDirectory, staging);
            if (!ContainsOpenRpaExtension(staging))
            {
                throw new InvalidOperationException("复制后的内置浏览器配置缺少 OpenRPA 扩展。");
            }

            File.WriteAllText(
                Path.Combine(staging, SourceStateFilename),
                JsonSerializer.Serialize(new BrowserProfileSeedState
                {
                    SourceDirectory = sourceDirectory,
                    ExtensionId = OpenRpaExtensionId,
                    InitialisedAtUtc = DateTime.UtcNow
                }));

            if (Directory.Exists(localDirectory))
            {
                try
                {
                    Directory.Move(localDirectory, backup);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException(
                        "无法更新 Maxwell 内置浏览器配置。请关闭所有由 Maxwell 打开的 Chrome 窗口后重试。",
                        ex);
                }
            }

            Directory.Move(staging, localDirectory);
            promoted = true;
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            return localDirectory;
        }
        catch
        {
            if (!Directory.Exists(localDirectory) && Directory.Exists(backup))
            {
                try { Directory.Move(backup, localDirectory); } catch { }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (promoted && Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        }
    }

    private static bool ContainsOpenRpaExtension(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

        string extensionRoot = Path.Combine(directory, "Default", "Extensions", OpenRpaExtensionId);
        if (!Directory.Exists(extensionRoot)) return false;

        try
        {
            return Directory.EnumerateFiles(extensionRoot, "manifest.json", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalCopyCurrent(
        string localDirectory,
        string localExecutable,
        string sourceDirectory,
        FileInfo sourceInfo)
    {
        string statePath = Path.Combine(localDirectory, SourceStateFilename);
        if (!File.Exists(localExecutable) || !File.Exists(statePath)) return false;

        try
        {
            ChromeSourceState? state = JsonSerializer.Deserialize<ChromeSourceState>(File.ReadAllText(statePath));
            return state is not null &&
                   string.Equals(state.SourceDirectory, sourceDirectory, StringComparison.OrdinalIgnoreCase) &&
                   state.ChromeLength == sourceInfo.Length &&
                   state.ChromeLastWriteTimeUtc == sourceInfo.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            string destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private static string? FindBundledRuntimeDirectory()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "runtime"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging"))
        ];
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "OpenRPA.NativeMessagingHost.exe")));
    }

    private static bool IsNetworkLocation(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ChromeSourceState
    {
        public string SourceDirectory { get; init; } = string.Empty;
        public long ChromeLength { get; init; }
        public DateTime ChromeLastWriteTimeUtc { get; init; }
    }

    private sealed class BrowserProfileSeedState
    {
        public string SourceDirectory { get; init; } = string.Empty;
        public string ExtensionId { get; init; } = string.Empty;
        public DateTime InitialisedAtUtc { get; init; }
    }
}
