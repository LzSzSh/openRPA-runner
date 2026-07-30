namespace OpenRpaWorkflowLauncher.Models;

/// <summary>
/// Optional administrator-managed defaults stored beside the shared Maxwell executable.
/// </summary>
public sealed class CompanyDeploymentSettings
{
    public string? SharedLibraryFolder { get; set; }
}
