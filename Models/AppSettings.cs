namespace OpenRpaWorkflowLauncher.Models;

public sealed class AppSettings
{
    public string? LastProjectFolder { get; set; }
    public string? RunHotkey { get; set; }
    public string? StopHotkey { get; set; }
    public string BrowserLaunchMode { get; set; } = "Bundled";
    public List<RecentProjectInfo> RecentProjects { get; set; } = [];
}

public sealed class RecentProjectInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
