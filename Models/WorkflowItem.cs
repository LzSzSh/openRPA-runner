namespace OpenRpaWorkflowLauncher.Models;

public sealed class WorkflowItem : OpenRpaWorkflowLauncher.ViewModels.ObservableObject
{
    private bool _isExecuting;
    private string _lastRunStatus = "未执行";

    public required string ProjectName { get; init; }
    public required string WorkflowName { get; init; }
    public required string ProjectAndName { get; init; }
    public required string Name { get; init; }
    public required string Filename { get; init; }
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string SourceFile { get; init; }
    public required WorkflowCompatibility Compatibility { get; init; }
    public bool IsManagedImport { get; init; }

    public string WorkflowIdArgument => $@"{ProjectName}\{Filename}";
    public string CompatibilityStatus => Compatibility.Status;
    public string CompatibilityDetails => Compatibility.Details;
    public bool CanRun => Compatibility.CanRun;

    public bool IsExecuting
    {
        get => _isExecuting;
        set => SetProperty(ref _isExecuting, value);
    }

    public string LastRunStatus
    {
        get => _lastRunStatus;
        set => SetProperty(ref _lastRunStatus, value);
    }
}
