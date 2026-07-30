using System.IO;
using System.Text.Json;
using OpenRpaWorkflowLauncher.Models;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class CompanyDeploymentSettingsService
{
    private const string SettingsFilename = "company-settings.json";

    public string? LoadSharedLibraryFolder()
    {
        string settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFilename);
        if (!File.Exists(settingsPath)) return null;

        try
        {
            CompanyDeploymentSettings? settings = JsonSerializer.Deserialize<CompanyDeploymentSettings>(File.ReadAllText(settingsPath));
            if (string.IsNullOrWhiteSpace(settings?.SharedLibraryFolder)) return null;

            return Path.IsPathRooted(settings.SharedLibraryFolder)
                ? Path.GetFullPath(settings.SharedLibraryFolder)
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, settings.SharedLibraryFolder));
        }
        catch
        {
            // A broken administrator config should not prevent a user from opening Maxwell.
            return null;
        }
    }
}
