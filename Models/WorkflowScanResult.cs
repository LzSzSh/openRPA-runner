using System.Collections.ObjectModel;

namespace OpenRpaWorkflowLauncher.Models;

public sealed class WorkflowScanResult
{
    public Collection<WorkflowItem> Workflows { get; } = [];
    public Collection<string> Warnings { get; } = [];
}
