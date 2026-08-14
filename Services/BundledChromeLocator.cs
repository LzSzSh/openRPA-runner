using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OpenRpaWorkflowLauncher.Services;

public static class BundledChromeLocator
{
    private const string OpenRpaExtensionId = "hpnihnhlcnfejboocnckgchjdofeaphe";
    private const string LocalChromeFolderName = "BundledChrome";
    private const string LocalNativeHostFolderName = "NativeMessagingHost";
    private const string SourceStateFilename = ".maxwell-source.json";
    private static readonly object LocalPreparationSync = new();

    public static string MaxwellBrowserProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Maxwell",
        "BrowserProfile");

    /// <summary>
    /// The Web Store copy of the OpenRPA extension uses the same native host
    /// name as the full OpenRPA desktop product.  Maxwell instead loads its
    /// private extension build from a local directory.  Keeping it out of the
    /// user profile also makes its MV3 service-worker wakeup deterministic.
    /// </summary>
    public static string EnsureLocalMaxwellBrowserExtensionDirectory()
    {
        string? runtime = FindBundledRuntimeDirectory();
        if (string.IsNullOrWhiteSpace(runtime))
        {
            throw new InvalidOperationException("未找到 Maxwell 浏览器扩展运行库。");
        }

        string sourceDirectory = Path.Combine(runtime, "maxwell-openrpa-extension");
        string sourceManifest = Path.Combine(sourceDirectory, "manifest.json");
        if (!File.Exists(sourceManifest))
        {
            throw new InvalidOperationException("Maxwell 浏览器扩展运行库不完整，缺少 manifest.json。");
        }

        string localDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Maxwell",
            "OpenRpaExtension");
        string localManifest = Path.Combine(localDirectory, "manifest.json");
        FileInfo sourceInfo = new(sourceManifest);
        if (IsLocalCopyCurrent(localDirectory, localManifest, sourceDirectory, sourceInfo))
        {
            return localDirectory;
        }

        string parent = Path.GetDirectoryName(localDirectory)!;
        string staging = Path.Combine(parent, ".OpenRpaExtension.sync-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".OpenRpaExtension.backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(parent);
            CopyDirectory(sourceDirectory, staging);
            File.WriteAllText(Path.Combine(staging, SourceStateFilename), JsonSerializer.Serialize(new ChromeSourceState
            {
                SourceDirectory = sourceDirectory,
                ChromeLength = sourceInfo.Length,
                ChromeLastWriteTimeUtc = sourceInfo.LastWriteTimeUtc
            }));
            if (Directory.Exists(localDirectory)) Directory.Move(localDirectory, backup);
            Directory.Move(staging, localDirectory);
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
        }
    }

    public static string? EnsureLocalNativeMessagingHostDirectory()
    {
        lock (LocalPreparationSync)
        {
            return EnsureLocalNativeMessagingHostDirectoryCore();
        }
    }

    private static string? EnsureLocalNativeMessagingHostDirectoryCore()
    {
        string? sourceRuntime = FindBundledRuntimeDirectory();
        if (string.IsNullOrWhiteSpace(sourceRuntime)) return null;

        string sourceHost = Path.Combine(sourceRuntime, "OpenRPA.NativeMessagingHost.exe");
        if (!File.Exists(sourceHost)) return null;

        string[] requiredFiles =
        [
            "OpenRPA.NativeMessagingHost.exe", "OpenRPA.NativeMessagingHost.exe.config",
            "OpenRPA.Interfaces.dll", "OpenRPA.NamedPipeWrapper.dll", "Newtonsoft.Json.dll",
            "FlaUI.Core.dll", "FlaUI.UIA3.dll", "NLog.dll"
        ];
        foreach (string file in requiredFiles)
        {
            if (!File.Exists(Path.Combine(sourceRuntime, file)))
                throw new InvalidOperationException("Maxwell Native Messaging 运行库不完整，缺少：" + file);
        }

        string localDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maxwell", LocalNativeHostFolderName);
        if (IsLocalCopyCurrent(localDirectory, Path.Combine(localDirectory, "OpenRPA.NativeMessagingHost.exe"), sourceRuntime, new FileInfo(sourceHost)) &&
            File.Exists(Path.Combine(localDirectory, "chromemanifest.template.json")) &&
            requiredFiles.All(file => File.Exists(Path.Combine(localDirectory, file))))
        {
            return localDirectory;
        }

        string parent = Path.GetDirectoryName(localDirectory)!;
        string staging = Path.Combine(parent, $".{LocalNativeHostFolderName}.sync-{Guid.NewGuid():N}");
        string backup = Path.Combine(parent, $".{LocalNativeHostFolderName}.backup-{Guid.NewGuid():N}");
        bool promoted = false;
        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(staging);
            foreach (string file in requiredFiles)
            {
                string source = Path.Combine(sourceRuntime, file);
                File.Copy(source, Path.Combine(staging, file), overwrite: true);
            }
            string sourceManifest = Path.Combine(sourceRuntime, "chromemanifest.template.json");
            if (!File.Exists(sourceManifest))
                sourceManifest = Path.Combine(sourceRuntime, "chromemanifest.json");
            if (!File.Exists(sourceManifest))
                throw new InvalidOperationException("Maxwell 运行库缺少 Chrome Native Messaging manifest。");
            File.Copy(
                sourceManifest,
                Path.Combine(staging, "chromemanifest.template.json"),
                overwrite: true);
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
                    // The source-state check above already proved this local copy
                    // is stale. Reusing a locked older host can leave the extension
                    // connected to a different named pipe and makes every browser
                    // workflow fail later with a misleading "not connected" error.
                    throw new InvalidOperationException(
                        "Maxwell 浏览器自动化组件需要更新，但正在被 Chrome 使用。请完全关闭 Chrome 后重试。");
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
        // A few portable-Chrome distributions include a root chrome.exe that
        // is only a launcher stub.  It exits successfully without creating a
        // browser when copied on its own.  The real Chromium executable lives
        // in the Chrome subdirectory and must always win this selection.
        string[] directCandidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome-portable", "chrome.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome", "chrome.exe")),
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome-portable", "Chrome", "chrome.exe"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome", "Chrome", "chrome.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome-portable", "Chrome", "chrome.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "runtime-staging", "chrome", "Chrome", "chrome.exe")),
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome-portable", "chrome.exe"),
            Path.Combine(AppContext.BaseDirectory, "runtime", "chrome", "chrome.exe")
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
    /// Chrome++ keeps a portable-launcher chrome.exe at the browser root and
    /// the actual Chromium engine in a versioned child directory. The launcher
    /// drops extension command-line switches, so automation must start the
    /// engine executable directly.
    /// </summary>
    public static string? FindChromiumEngineExecutable(string? chromeRoot)
    {
        if (string.IsNullOrWhiteSpace(chromeRoot) || !Directory.Exists(chromeRoot)) return null;

        bool IsEngineDirectory(string directory) =>
            File.Exists(Path.Combine(directory, "chrome.exe")) &&
            File.Exists(Path.Combine(directory, "chrome.dll")) &&
            File.Exists(Path.Combine(directory, "icudtl.dat")) &&
            Directory.Exists(Path.Combine(directory, "Locales"));

        if (IsEngineDirectory(chromeRoot)) return Path.Combine(chromeRoot, "chrome.exe");
        try
        {
            string? engineDirectory = Directory.EnumerateDirectories(chromeRoot)
                .FirstOrDefault(IsEngineDirectory);
            return engineDirectory is null ? null : Path.Combine(engineDirectory, "chrome.exe");
        }
        catch
        {
            return null;
        }
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
        if (!IsNetworkLocation(sourceDirectory))
        {
            ConfigureChromePlusRuntime(sourceDirectory);
            return sourceDirectory;
        }

        string sourceExecutable = Path.Combine(sourceDirectory, "chrome.exe");
        if (!File.Exists(sourceExecutable)) return null;

        FileInfo sourceInfo = new(sourceExecutable);
        string localDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Maxwell",
            LocalChromeFolderName);
        string localExecutable = Path.Combine(localDirectory, "chrome.exe");
        if (IsLocalCopyCurrent(localDirectory, localExecutable, sourceDirectory, sourceInfo) &&
            IsCompleteChromiumDirectory(localDirectory, localExecutable))
        {
            ConfigureChromePlusRuntime(localDirectory);
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
            ConfigureChromePlusRuntime(localDirectory);
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

    private static bool IsCompleteChromiumDirectory(string directory, string executable)
    {
        // chrome.exe alone is not a browser runtime.  This also identifies a
        // stale copy from earlier Maxwell packages that mirrored only a
        // portable-launcher stub instead of Chromium itself.
        if (!File.Exists(executable)) return false;

        // Standard Chromium keeps these beside chrome.exe. Chrome++ portable
        // packages keep the engine in a versioned child directory instead.
        bool IsEngineDirectory(string candidate) =>
            File.Exists(Path.Combine(candidate, "chrome.dll")) &&
            File.Exists(Path.Combine(candidate, "icudtl.dat")) &&
            Directory.Exists(Path.Combine(candidate, "Locales"));

        if (IsEngineDirectory(directory)) return true;
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Any(IsEngineDirectory);
        }
        catch
        {
            return false;
        }
    }

    private static void ConfigureChromePlusRuntime(string chromeDirectory)
    {
        string iniPath = Path.Combine(chromeDirectory, "chrome++.ini");
        if (!File.Exists(iniPath)) return;

        // Windows.GetElement relies on Chrome exposing a UI Automation tree.
        // Chrome++ is the portable launcher actually used by Maxwell, so keep
        // this switch in its persistent configuration as well as in the
        // workflow launcher command line. This avoids a cold-start race where
        // Chromium creates its accessibility tree only after the first lookup.
        // Remove the obsolete private-extension switches from experimental
        // packages: current Maxwell browser compatibility is intentionally
        // based on the baseline profile, not a side-loaded extension.
        string ini = File.ReadAllText(iniPath);
        string updated = System.Text.RegularExpressions.Regex.Replace(
            ini,
            "\\s+--disable-extensions-except=(?:\\\"[^\\\"]*\\\"|\\S+)|\\s+--load-extension=(?:\\\"[^\\\"]*\\\"|\\S+)",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (System.Text.RegularExpressions.Regex.IsMatch(updated, @"(?m)^command_line="))
        {
            updated = new System.Text.RegularExpressions.Regex(@"(?m)^command_line=(.*)$").Replace(
                updated,
                match => match.Groups[1].Value.IndexOf("--force-renderer-accessibility", StringComparison.OrdinalIgnoreCase) >= 0
                    ? match.Value
                    : "command_line=" + match.Groups[1].Value.Trim() + " --force-renderer-accessibility",
                1);
        }
        if (!string.Equals(ini, updated, StringComparison.Ordinal))
        {
            File.WriteAllText(iniPath, updated, new UTF8Encoding(false));
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
