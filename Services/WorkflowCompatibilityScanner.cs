using System.Xml.Linq;
using OpenRpaWorkflowLauncher.Models;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class WorkflowCompatibilityScanner
{
    private sealed record ModuleRule(string Assembly, string DisplayName, string Status, string Detail, bool CanRun);

    private static readonly ModuleRule[] Rules =
    [
        new("OpenRPA.Windows", "Windows UI 自动化", "已验证（基础）", "已通过记事本真实流程验证；选择器仍与当前应用版本和界面状态相关。", true),
        new("OpenRPA.Forms", "桌面通知与表单", "已验证（基础）", "已通过 ShowNotification 真实流程验证；交互式 InvokeForm 尚未单独验收。", true),
        new("OpenRPA.Utilities", "通用工具", "已验证（基础）", "已验证 StartProcess、CreateDataTable、AddDataRow、WriteCSV、ReadCSV、CompressArchive、ExpandArchive、WriteExcel；其余活动按首次使用记录。", true),
        new("OpenRPA", "OpenRPA 核心", "已验证（基础）", "已验证 CopyClipboard、InsertClipboard、点击、输入和子 workflow；其余核心活动按首次使用记录。", true),
        new("OpenRPA.NM", "浏览器自动化", "需要外部环境", "已验证 OpenURL 与 ExecuteScript；目标电脑仍必须安装并启用 OpenRPA 浏览器扩展、Native Messaging Host，并为 file URL 开启扩展访问权限。", true),
        new("OpenRPA.Office", "Office COM 自动化", "需要外部环境", "需要目标电脑安装兼容的 Microsoft Office 和 COM 组件；当前设计器未加载 Office 插件，尚未验收。", true),
        new("OpenRPA.Image", "图像自动化", "已验证（基础）", "已验证获得元素、图像匹配与点击；对分辨率、缩放比例、窗口外观敏感。OCR 仍需另行验收，因为它需要 tessdata 语言模型。", true),
        new("OpenRPA.Script", "脚本活动", "高风险", "可能执行脚本或代码；仅运行可信来源 workflow。", true),
        new("OpenRPA.Java", "Java 自动化", "当前不支持", "Desktop 发布包未包含 Java Bridge。", false),
        new("OpenRPA.SAP", "SAP 自动化", "当前不支持", "Desktop 发布包未包含 SAP 运行环境。", false)
    ];

    public WorkflowCompatibility Analyze(string xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml)) return Result("无法扫描", "workflow 缺少 Xaml。", false, []);

        HashSet<string> assemblies = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> activities = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            XDocument document = XDocument.Parse(xaml);
            foreach (XElement element in document.Root?.DescendantsAndSelf() ?? [])
            {
                string ns = element.Name.NamespaceName;
                const string marker = "assembly=";
                int index = ns.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;

                string assembly = ns[(index + marker.Length)..].Split(';')[0].Trim();
                if (string.IsNullOrWhiteSpace(assembly)) continue;
                assemblies.Add(assembly);
                if (assembly.StartsWith("OpenRPA", StringComparison.OrdinalIgnoreCase))
                    activities.Add(assembly + "." + element.Name.LocalName);
            }
        }
        catch (Exception ex)
        {
            return Result("无法扫描", "Xaml 解析失败：" + ex.Message, false, []);
        }

        List<ModuleRule> found = Rules.Where(rule => assemblies.Contains(rule.Assembly)).ToList();
        List<string> unknown = assemblies
            .Where(assembly => assembly.StartsWith("OpenRPA", StringComparison.OrdinalIgnoreCase) && Rules.All(rule => !string.Equals(rule.Assembly, assembly, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(value => value)
            .ToList();
        List<string> details = found.Select(rule => rule.DisplayName + "：" + rule.Detail).ToList();
        if (unknown.Count > 0) details.Add("未知 OpenRPA 模块：" + string.Join("、", unknown) + "，需要首次真实验收。");
        if (activities.Count > 0) details.Insert(0, "检测到活动：" + string.Join("、", activities.OrderBy(value => value)));

        bool canRun = found.All(rule => rule.CanRun);
        string status = !canRun ? "当前不支持"
            : found.Any(rule => rule.Status == "高风险") ? "高风险"
            : found.Any(rule => rule.Status == "需要外部环境") ? "需要外部环境"
            : unknown.Count > 0 ? "未验证"
            : found.Count == 0 ? "基础 WWF"
            : "已验证（基础）";

        return Result(
            status,
            details.Count == 0 ? "仅使用 Windows Workflow Foundation 内置活动。" : string.Join(Environment.NewLine, details),
            canRun,
            found.Select(rule => rule.DisplayName).Concat(unknown).ToList(),
            activities.OrderBy(value => value).ToList());
    }

    private static WorkflowCompatibility Result(string status, string details, bool canRun, IReadOnlyList<string> modules, IReadOnlyList<string>? activities = null) => new()
    {
        Status = status,
        Details = details,
        CanRun = canRun,
        Modules = modules,
        Activities = activities ?? []
    };
}
