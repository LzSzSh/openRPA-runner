namespace OpenRpaWorkflowLauncher.Models;

public sealed class RecentProjectItem : OpenRpaWorkflowLauncher.ViewModels.ObservableObject
{
    private bool _isSelected;

    public required string Name { get; init; }
    public required string Path { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
