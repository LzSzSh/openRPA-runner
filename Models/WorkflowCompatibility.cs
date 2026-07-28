namespace OpenRpaWorkflowLauncher.Models;

public sealed class WorkflowCompatibility
{
    public required string Status { get; init; }
    public required string Details { get; init; }
    public required bool CanRun { get; init; }
    public required IReadOnlyList<string> Modules { get; init; }
    public required IReadOnlyList<string> Activities { get; init; }
}
