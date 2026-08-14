using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class ChromeAutomationStatusService
{
    private const string OpenRpaExtensionId = "hpnihnhlcnfejboocnckgchjdofeaphe";
    private const string MaxwellNativeHostName = "com.maxwell.openrpa.msg";
    private static readonly string[] MaxwellNativeHostRegistryPaths =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\com.maxwell.openrpa.msg",
        @"Software\Microsoft\Edge\NativeMessagingHosts\com.maxwell.openrpa.msg"
    ];
    private static readonly string[] OfficialNativeHostRegistryPaths =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\com.openrpa.msg",
        @"Software\Microsoft\Edge\NativeMessagingHosts\com.openrpa.msg"
    ];

    public void RegisterBundledNativeHost(string? runtimeDirectory = null)
    {
        runtimeDirectory ??= Path.Combine(AppContext.BaseDirectory, "runtime");
        string hostExecutable = Path.Combine(runtimeDirectory, "OpenRPA.NativeMessagingHost.exe");
        string manifestPath = Path.Combine(runtimeDirectory, "chromemanifest.json");
        string templatePath = Path.Combine(runtimeDirectory, "chromemanifest.template.json");

        if (!File.Exists(hostExecutable))
        {
            throw new FileNotFoundException("未找到 Maxwell 自带的 Native Messaging Host。请确认使用的是完整发布包。", hostExecutable);
        }

        string sourcePath = File.Exists(templatePath) ? templatePath : manifestPath;
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("未找到 Chrome Native Messaging manifest 模板。请重新生成 Maxwell 发布包。", sourcePath);
        }

        string template = File.ReadAllText(sourcePath);
        if (!Regex.IsMatch(template, @"""path""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(template, @"""name""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Native Messaging manifest 格式无效，缺少 name 或 path。");
        }
        string escapedHostPath = hostExecutable.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string manifest = Regex.Replace(
            template,
            @"(""path""\s*:\s*"")[^""]*("")",
            match => match.Groups[1].Value + escapedHostPath + match.Groups[2].Value,
            RegexOptions.CultureInvariant);
        manifest = Regex.Replace(
            manifest,
            @"(""name""\s*:\s*"")[^""]*("")",
            match => match.Groups[1].Value + MaxwellNativeHostName + match.Groups[2].Value,
            RegexOptions.CultureInvariant);
        if (!manifest.Contains(escapedHostPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Native Messaging manifest 格式无效，无法写入 Host 路径。");
        }

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        foreach (string registryPath in MaxwellNativeHostRegistryPaths)
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(registryPath);
            key?.SetValue(string.Empty, manifestPath, RegistryValueKind.String);
        }
    }

    public BrowserAutomationCheckResult SwitchToRuntimeMode(bool useBundledBrowser)
    {
        BrowserAutomationCheckResult result = CheckAndPrepare(useBundledBrowser);
        return result.IsReady
            ? BrowserAutomationCheckResult.Ready("已切换到运行模式。Maxwell 已接管浏览器自动化；请完全关闭并重新打开 Chrome 后再执行流程。")
            : result;
    }

    public BrowserAutomationCheckResult SwitchToEditMode()
    {
        try
        {
            string? officialManifestPath = FindOfficialOpenRpaManifest();
            if (string.IsNullOrWhiteSpace(officialManifestPath))
            {
                return BrowserAutomationCheckResult.Failed(
                    "无法切换到编辑模式：未找到官方 OpenRPA 的 Chrome Native Messaging manifest。请先安装或修复 OpenRPA。");
            }

            foreach (string registryPath in OfficialNativeHostRegistryPaths)
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(registryPath);
                key?.SetValue(string.Empty, officialManifestPath, RegistryValueKind.String);
            }

            return BrowserAutomationCheckResult.Ready(
                "已切换到编辑模式。项目执行已禁用；请完全退出 Maxwell RuntimeHost 和 Chrome，启动 OpenRPA 后重新打开 Chrome，等待 NM: online 再录制或高亮元素。");
        }
        catch (Exception ex)
        {
            return BrowserAutomationCheckResult.Failed("切换到编辑模式失败：" + ex.Message);
        }
    }

    public BrowserAutomationCheckResult CheckAndPrepare(bool useBundledBrowser)
    {
        try
        {
            string? nativeHostDirectory = BundledChromeLocator.EnsureLocalNativeMessagingHostDirectory();
            if (string.IsNullOrWhiteSpace(nativeHostDirectory))
            {
                return BrowserAutomationCheckResult.Failed("未找到内置 Native Messaging Host；请确认使用的是完整 Maxwell 发布包。");
            }

            RegisterBundledNativeHost(nativeHostDirectory);
            if (!HasMaxwellNativeMessagingRegistration())
            {
                return BrowserAutomationCheckResult.Failed("Native Messaging 注册未完成；请重新点击检查，或以当前 Windows 用户运行执行器。");
            }

            if (useBundledBrowser)
            {
                string profileDirectory = BundledChromeLocator.EnsureLocalBundledBrowserProfileDirectory();
                if (!HasOpenRpaExtension(profileDirectory))
                {
                    return BrowserAutomationCheckResult.Failed("内置 Chrome 配置中未检测到 OpenRPA 扩展；请重新解压完整发布包。");
                }

                return BrowserAutomationCheckResult.Ready(
                    "内置 OpenRPA 浏览器扩展和 Native Messaging 已就绪。若 Chrome 正在运行，请先完全关闭所有 Chrome 窗口再执行流程。");
            }

            if (!HasOpenRpaExtension(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Google", "Chrome", "User Data")))
            {
                return BrowserAutomationCheckResult.Failed(
                    "本机 Chrome 未检测到 OpenRPA 扩展。请安装并启用官方扩展，然后完全关闭并重新打开 Chrome。");
            }

            return BrowserAutomationCheckResult.Ready(
                "已检测到本机 Chrome 的 OpenRPA 扩展，Native Messaging 也已注册。请确保 Chrome 已完全重启。");
        }
        catch (Exception ex)
        {
            return BrowserAutomationCheckResult.Failed("浏览器自动化检查失败：" + ex.Message);
        }
    }

    private static bool HasOpenRpaExtension(string profileRoot)
    {
        if (!Directory.Exists(profileRoot)) return false;
        try
        {
            return Directory.EnumerateFiles(
                profileRoot,
                "manifest.json",
                SearchOption.AllDirectories)
                .Any(path => path.IndexOf($"Extensions{Path.DirectorySeparatorChar}{OpenRpaExtensionId}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMaxwellNativeMessagingRegistration()
    {
        return MaxwellNativeHostRegistryPaths.Any(registryPath =>
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(registryPath);
            string? manifestPath = key?.GetValue(string.Empty) as string;
            return !string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath);
        });
    }

    private static string? FindOfficialOpenRpaManifest()
    {
        foreach (string registryPath in OfficialNativeHostRegistryPaths)
        {
            using RegistryKey? currentUserKey = Registry.CurrentUser.OpenSubKey(registryPath);
            string? currentUserPath = currentUserKey?.GetValue(string.Empty) as string;
            if (IsOfficialOpenRpaManifest(currentUserPath)) return currentUserPath;

            using RegistryKey? localMachineKey = Registry.LocalMachine.OpenSubKey(registryPath);
            string? localMachinePath = localMachineKey?.GetValue(string.Empty) as string;
            if (IsOfficialOpenRpaManifest(localMachinePath)) return localMachinePath;
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRPA", "chromemanifest.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRPA", "chromemanifest.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRPA", "chromemanifest.json")
        ];
        return candidates.FirstOrDefault(IsOfficialOpenRpaManifest);
    }

    private static bool IsOfficialOpenRpaManifest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            string content = File.ReadAllText(path);
            return Regex.IsMatch(
                content,
                @"""name""\s*:\s*""com\.openrpa\.msg""",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class BrowserAutomationCheckResult
{
    public bool IsReady { get; init; }
    public string Message { get; init; } = string.Empty;

    public static BrowserAutomationCheckResult Ready(string message) => new() { IsReady = true, Message = message };
    public static BrowserAutomationCheckResult Failed(string message) => new() { IsReady = false, Message = message };
}
