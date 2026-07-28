using System.IO;
using System.Text.Json;

namespace OpenRpaWorkflowLauncher.Models;

public enum BrowserModeAvailability
{
    Both,
    BundledOnly,
    LocalOnly
}

public sealed class BrowserModePolicy
{
    private const string ConfigurationFileName = "browser-mode.json";

    public BrowserModeAvailability Availability { get; init; } = BrowserModeAvailability.Both;

    public static BrowserModePolicy Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName);
        if (!File.Exists(path)) return new BrowserModePolicy();

        try
        {
            BrowserModePolicyFile? file = JsonSerializer.Deserialize<BrowserModePolicyFile>(File.ReadAllText(path));
            return file?.BrowserMode switch
            {
                "BundledOnly" => new BrowserModePolicy { Availability = BrowserModeAvailability.BundledOnly },
                "LocalOnly" => new BrowserModePolicy { Availability = BrowserModeAvailability.LocalOnly },
                _ => new BrowserModePolicy()
            };
        }
        catch
        {
            return new BrowserModePolicy();
        }
    }

    private sealed class BrowserModePolicyFile
    {
        public string? BrowserMode { get; set; }
    }
}
