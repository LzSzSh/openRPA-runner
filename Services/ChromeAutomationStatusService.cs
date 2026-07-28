using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenRpaWorkflowLauncher.Models;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class ChromeAutomationStatusService
{
    private const string NativeHostRegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\com.openrpa.msg";

    // OpenRPA has used more than one Chrome Web Store extension id over time.
    // Detect all ids known to the bundled OpenRPA runtime instead of assuming one id.
    private static readonly string[] KnownExtensionIds =
    [
        "jkgopbngpkjbhflaahonaligngilgped",
        "cjjehhadngahcdkbkeopdlmjedddkedh", "meoobaegjobjgfnlfndegpnpdmonbnbe",
        "eglkkjllkdooicijpbolleoemogkagbp", "ijabkdeadobnodfdgilkjhbploikblbg",
        "cfhdojbkjhnklbpkdaibdccddilifddb", "hpnihnhlcnfejboocnckgchjdofeaphe",
        "ebbbjigjoglkolagcfjdnginmfknnmmg", "ennlpladclaaogmhlddghpneajafmgln",
        "fdpjjldghhdjaakadnkiepghjdfjllmg", "hkjbghcanbbkhldlfkddbiiooadknael",
        "igkhnjpllpckiodjdkoiailagikebloa", "bnmpdfndpadhamjmmkgkhgkmplfancbi"
    ];

    public ChromeAutomationStatus Inspect()
    {
        string? chromePath = FindChromeExecutable();
        string? extensionProfile = FindExtensionProfile();
        string? manifestPath = ReadNativeHostManifestPath();
        bool hostRegistered = !string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath);

        return new ChromeAutomationStatus
        {
            IsChromeInstalled = !string.IsNullOrWhiteSpace(chromePath),
            ChromePath = chromePath,
            IsExtensionDetected = !string.IsNullOrWhiteSpace(extensionProfile),
            ExtensionProfile = extensionProfile,
            IsNativeHostRegistered = hostRegistered,
            NativeHostPath = manifestPath
        };
    }

    public void RegisterBundledNativeHost()
    {
        string runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtime");
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
        string escapedHostPath = hostExecutable.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string manifest = Regex.Replace(
            template,
            @"(""path""\s*:\s*"")[^""]*("")",
            match => match.Groups[1].Value + escapedHostPath + match.Groups[2].Value,
            RegexOptions.CultureInvariant);

        if (manifest == template)
        {
            throw new InvalidOperationException("Native Messaging manifest 格式无效，无法写入 Host 路径。");
        }

        File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(NativeHostRegistryPath);
        key?.SetValue(string.Empty, manifestPath, RegistryValueKind.String);
    }

    private static string? FindChromeExecutable()
    {
        string? bundled = BundledChromeLocator.FindBundledChromeExecutable();
        if (!string.IsNullOrWhiteSpace(bundled) && File.Exists(bundled)) return bundled;

        foreach (RegistryKey baseKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key?.GetValue(string.Empty) is string candidate && File.Exists(candidate)) return candidate;
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindExtensionProfile()
    {
        string maxwellProfile = BundledChromeLocator.MaxwellBrowserProfileDirectory;
        string? maxwellExtension = FindExtensionProfileUnder(maxwellProfile, allowDirectExtensionsRoot: true);
        if (!string.IsNullOrWhiteSpace(maxwellExtension)) return "Maxwell 内置浏览器/" + maxwellExtension;

        string? bundledProfileRoot = BundledChromeLocator.FindBundledChromeProfileRoot();
        if (!string.IsNullOrWhiteSpace(bundledProfileRoot))
        {
            string? bundledProfile = FindExtensionProfileUnder(bundledProfileRoot, allowDirectExtensionsRoot: true);
            if (!string.IsNullOrWhiteSpace(bundledProfile)) return "内置浏览器/" + bundledProfile;
        }

        string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
        if (!Directory.Exists(userData)) return null;

        return FindExtensionProfileUnder(userData, allowDirectExtensionsRoot: false);
    }

    private static string? FindExtensionProfileUnder(string root, bool allowDirectExtensionsRoot)
    {
        if (!Directory.Exists(root)) return null;

        if (allowDirectExtensionsRoot)
        {
            string directExtensions = Path.Combine(root, "Extensions");
            if (ContainsKnownExtension(directExtensions)) return Path.GetFileName(root);
        }

        foreach (string profile in Directory.EnumerateDirectories(root))
        {
            string profileName = Path.GetFileName(profile);
            if (profileName != "Default" && !profileName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) && profileName != "Guest Profile") continue;

            string extensionsRoot = Path.Combine(profile, "Extensions");
            if (ContainsKnownExtension(extensionsRoot)) return profileName;
        }

        return null;
    }

    private static bool ContainsKnownExtension(string extensionsRoot)
    {
        return Directory.Exists(extensionsRoot) && KnownExtensionIds.Any(id => Directory.Exists(Path.Combine(extensionsRoot, id)));
    }

    private static string? ReadNativeHostManifestPath()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(NativeHostRegistryPath);
        return key?.GetValue(string.Empty) as string;
    }
}
