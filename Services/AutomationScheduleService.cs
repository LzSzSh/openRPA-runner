using System.IO;
using System.Text.Json;
using OpenRpaWorkflowLauncher.Models;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class AutomationScheduleService
{
    private readonly string _schedulePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AutomationScheduleService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "OpenRpaWorkflowLauncher");
        Directory.CreateDirectory(appFolder);
        _schedulePath = Path.Combine(appFolder, "automations.json");
    }

    public IReadOnlyList<ScheduledAutomation> Load()
    {
        if (!File.Exists(_schedulePath)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<ScheduledAutomation>>(File.ReadAllText(_schedulePath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<ScheduledAutomation> automations)
    {
        string json = JsonSerializer.Serialize(automations, JsonOptions);
        string temporaryPath = _schedulePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _schedulePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
