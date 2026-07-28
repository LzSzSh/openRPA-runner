using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class ChromeAutomationStatusService
{
    private const string NativeHostRegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\com.openrpa.msg";

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
}
