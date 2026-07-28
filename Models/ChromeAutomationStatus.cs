namespace OpenRpaWorkflowLauncher.Models;

public sealed class ChromeAutomationStatus
{
    public bool IsChromeInstalled { get; init; }
    public string? ChromePath { get; init; }
    public bool IsExtensionDetected { get; init; }
    public string? ExtensionProfile { get; init; }
    public bool IsNativeHostRegistered { get; init; }
    public string? NativeHostPath { get; init; }

    public string Summary
    {
        get
        {
            if (!IsChromeInstalled) return "未检测到 Google Chrome";
            if (!IsExtensionDetected) return "Chrome 已安装，但未检测到 OpenRPA 扩展";
            if (!IsNativeHostRegistered) return "扩展已检测到，但 Native Messaging Host 未配置";
            return "Chrome 浏览器自动化已配置";
        }
    }

    public string Detail
    {
        get
        {
            if (!IsChromeInstalled)
            {
                return "请先安装 Google Chrome；安装完成后点击“重新检查”。";
            }

            if (!IsExtensionDetected)
            {
                return "请通过“打开扩展页”安装并启用 OpenRPA 扩展；安装后完全退出并重新打开 Chrome。";
            }

            if (!IsNativeHostRegistered)
            {
                return "点击“修复 Host 注册”后，完全退出并重新打开 Chrome。";
            }

            return "已检测到 Chrome、OpenRPA 扩展和 Native Messaging Host。首次使用或更新后，请完全退出并重新打开 Chrome。";
        }
    }

    public string BadgeBackground => IsChromeInstalled && IsExtensionDetected && IsNativeHostRegistered
        ? "#DCFCE7"
        : "#FEF3C7";

    public string BadgeForeground => IsChromeInstalled && IsExtensionDetected && IsNativeHostRegistered
        ? "#166534"
        : "#92400E";
}
