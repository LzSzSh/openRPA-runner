using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using OpenRpaWorkflowLauncher.Models;
using OpenRpaWorkflowLauncher.Services;

namespace OpenRpaWorkflowLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettingsService _settingsService = new();
    private readonly WorkflowScanner _workflowScanner = new();
    private readonly MaxwellRuntimeRunner _runtimeRunner = new();
    private readonly CompanyDeploymentSettingsService _companyDeploymentSettingsService = new();
    private readonly AppSettings _settings;
    private readonly BrowserModePolicy _browserModePolicy = BrowserModePolicy.Load();

    private string? _projectFolder;
    private string? _sharedLibraryFolder;
    private string _sharedLibraryStatus = "未配置共享工作流目录";
    private string _currentProjectName = "未选择 Project";
    private string _statusText = "空闲";
    private string _statusKind = "Idle";
    private string? _searchText;
    private string? _recentProjectSearchText;
    private string _activeView = "Home";
    private string? _runHotkey;
    private string? _stopHotkey;
    private string? _hotkeyError;
    private bool _isRunning;
    private bool _stopRequested;
    private WorkflowItem? _activeWorkflow;
    private bool _isRecentExpanded;
    private bool _isRecentSearchVisible;
    private bool _isRecentHeaderSelected;
    private bool _useBundledBrowser;

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        _sharedLibraryFolder = _settings.SharedLibraryFolder
            ?? _companyDeploymentSettingsService.LoadSharedLibraryFolder();
        _runHotkey = _settings.RunHotkey;
        _stopHotkey = _settings.StopHotkey;
        _useBundledBrowser = _browserModePolicy.Availability != BrowserModeAvailability.LocalOnly;

        BrowseSharedLibraryCommand = new RelayCommand(_ => BrowseSharedLibraryFolder());
        SyncSharedLibraryCommand = new AsyncRelayCommand(RefreshSharedLibraryAsync, _ => !IsRunning && !string.IsNullOrWhiteSpace(SharedLibraryFolder));
        RefreshCommand = new RelayCommand(_ => LoadWorkflows(), _ => !IsRunning);
        RunFirstWorkflowCommand = new AsyncRelayCommand(RunFirstWorkflowAsync, _ => !IsRunning && Workflows.Count > 0);
        RunWorkflowCommand = new AsyncRelayCommand(RunWorkflowAsync, parameter => !IsRunning && parameter is WorkflowItem workflow && workflow.CanRun);
        StopWorkflowCommand = new AsyncRelayCommand(StopWorkflowAsync, _ => IsRunning);
        ClearLogsCommand = new RelayCommand(_ => Logs.Clear());
        ShowRecentProjectsCommand = new RelayCommand(_ => ShowRecentProjects());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        ClearRunHotkeyCommand = new RelayCommand(_ => SetRunHotkey(null));
        ClearStopHotkeyCommand = new RelayCommand(_ => SetStopHotkey(null));
        SelectRecentProjectCommand = new RelayCommand(SelectRecentProject);
        ToggleRecentProjectSearchCommand = new RelayCommand(_ => ToggleRecentProjectSearch());
        WorkflowView = CollectionViewSource.GetDefaultView(Workflows);
        WorkflowView.Filter = FilterWorkflow;
        RecentProjectView = CollectionViewSource.GetDefaultView(RecentProjects);
        RecentProjectView.Filter = FilterRecentProject;
        SynchronizeRecentProjects();
        LoadWorkflows();
    }

    public ObservableCollection<WorkflowItem> Workflows { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];
    public ICollectionView WorkflowView { get; }
    public ICollectionView RecentProjectView { get; }

    public string? ProjectFolder
    {
        get => _projectFolder;
        set => SetProperty(ref _projectFolder, value);
    }

    public string? SharedLibraryFolder
    {
        get => _sharedLibraryFolder;
        set
        {
            if (SetProperty(ref _sharedLibraryFolder, value))
            {
                // The sidebar represents the configured shared library, not a
                // cross-library history. A changed root must never keep projects
                // from the previous network path visible or selected.
                ClearProjectsForSharedLibraryChange();
                SharedLibraryStatus = string.IsNullOrWhiteSpace(value)
                    ? "未配置共享工作流目录"
                    : "路径已保存；点击“立即刷新”读取共享项目。";
                SaveSettings();
                SyncSharedLibraryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SharedLibraryStatus
    {
        get => _sharedLibraryStatus;
        private set => SetProperty(ref _sharedLibraryStatus, value);
    }

    public string CurrentProjectName
    {
        get => _currentProjectName;
        private set => SetProperty(ref _currentProjectName, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                WorkflowView.Refresh();
                OnPropertyChanged(nameof(VisibleWorkflowCount));
            }
        }
    }

    public string? RecentProjectSearchText
    {
        get => _recentProjectSearchText;
        set
        {
            if (SetProperty(ref _recentProjectSearchText, value))
            {
                RecentProjectView.Refresh();
            }
        }
    }

    public string ActiveView
    {
        get => _activeView;
        private set
        {
            if (SetProperty(ref _activeView, value))
            {
                OnPropertyChanged(nameof(IsHomeVisible));
                OnPropertyChanged(nameof(IsWorkflowVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsRecentProjectsSelected));
                OnPropertyChanged(nameof(IsSettingsSelected));
            }
        }
    }

    public Visibility IsHomeVisible => ActiveView == "Home" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsWorkflowVisible => ActiveView == "Workflow" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsSettingsVisible => ActiveView == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    public bool IsRecentProjectsSelected => _isRecentHeaderSelected;
    public bool IsSettingsSelected => ActiveView == "Settings";

    public string RunHotkeyText => string.IsNullOrWhiteSpace(RunHotkey) ? "未设置" : RunHotkey;
    public string StopHotkeyText => string.IsNullOrWhiteSpace(StopHotkey) ? "未设置" : StopHotkey;
    public string RunButtonText => string.IsNullOrWhiteSpace(RunHotkey) ? "运行" : $"运行（{RunHotkey}）";
    public string StopButtonText => string.IsNullOrWhiteSpace(StopHotkey) ? "停止" : $"停止（{StopHotkey}）";

    public string? RunHotkey
    {
        get => _runHotkey;
        private set
        {
            if (SetProperty(ref _runHotkey, value))
            {
                OnPropertyChanged(nameof(RunHotkeyText));
                OnPropertyChanged(nameof(RunButtonText));
                SaveSettings();
            }
        }
    }

    public string? StopHotkey
    {
        get => _stopHotkey;
        private set
        {
            if (SetProperty(ref _stopHotkey, value))
            {
                OnPropertyChanged(nameof(StopHotkeyText));
                OnPropertyChanged(nameof(StopButtonText));
                SaveSettings();
            }
        }
    }

    public string? HotkeyError
    {
        get => _hotkeyError;
        private set => SetProperty(ref _hotkeyError, value);
    }

    public bool IsRecentExpanded
    {
        get => _isRecentExpanded;
        private set => SetProperty(ref _isRecentExpanded, value);
    }

    public bool IsRecentSearchVisible
    {
        get => _isRecentSearchVisible;
        private set => SetProperty(ref _isRecentSearchVisible, value);
    }

    public int WorkflowCount => Workflows.Count;

    public int VisibleWorkflowCount
    {
        get
        {
            int count = 0;
            foreach (object _ in WorkflowView)
            {
                count++;
            }

            return count;
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RunFirstWorkflowCommand.RaiseCanExecuteChanged();
                RunWorkflowCommand.RaiseCanExecuteChanged();
                SyncSharedLibraryCommand.RaiseCanExecuteChanged();
                StopWorkflowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand BrowseSharedLibraryCommand { get; }
    public AsyncRelayCommand SyncSharedLibraryCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RunFirstWorkflowCommand { get; }
    public AsyncRelayCommand RunWorkflowCommand { get; }
    public AsyncRelayCommand StopWorkflowCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand ShowRecentProjectsCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ClearRunHotkeyCommand { get; }
    public RelayCommand ClearStopHotkeyCommand { get; }
    public RelayCommand SelectRecentProjectCommand { get; }
    public RelayCommand ToggleRecentProjectSearchCommand { get; }

    public bool TrySetHotkey(string target, string key)
    {
        if (target == "Run")
        {
            return SetRunHotkey(key);
        }

        if (target == "Stop")
        {
            return SetStopHotkey(key);
        }

        return false;
    }

    public async Task TryRunShortcutAsync(string key)
    {
        if (!string.IsNullOrWhiteSpace(RunHotkey) &&
            string.Equals(RunHotkey, key, StringComparison.OrdinalIgnoreCase) &&
            RunFirstWorkflowCommand.CanExecute(null))
        {
            await RunFirstWorkflowAsync(null);
        }
    }

    public async Task TryStopShortcutAsync(string key)
    {
        if (!string.IsNullOrWhiteSpace(StopHotkey) &&
            string.Equals(StopHotkey, key, StringComparison.OrdinalIgnoreCase) &&
            StopWorkflowCommand.CanExecute(null))
        {
            await StopWorkflowAsync(null);
        }
    }

    private void BrowseSharedLibraryFolder()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new()
        {
            Title = "选择网络共享工作流库的根目录",
            InitialDirectory = Directory.Exists(SharedLibraryFolder) ? SharedLibraryFolder : string.Empty,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            SharedLibraryFolder = dialog.FolderName;
        }
    }

    private int LoadWorkflows(bool activateWorkflowView = true)
    {
        SynchronizeRecentProjects();
        Workflows.Clear();
        Warnings.Clear();
        OnPropertyChanged(nameof(WorkflowCount));

        if (!string.IsNullOrWhiteSpace(ProjectFolder) && Directory.Exists(ProjectFolder))
        {
            AddWorkflowsFromFolder(ProjectFolder, null);
        }

        if (!string.IsNullOrWhiteSpace(SharedLibraryFolder) && !Directory.Exists(SharedLibraryFolder))
        {
            Warnings.Add($"共享工作流目录不可访问：{SharedLibraryFolder}");
        }

        WorkflowView.Refresh();
        OnPropertyChanged(nameof(WorkflowCount));
        OnPropertyChanged(nameof(VisibleWorkflowCount));
        RunFirstWorkflowCommand.RaiseCanExecuteChanged();

        CurrentProjectName = Workflows.Count == 0 && string.IsNullOrWhiteSpace(ProjectFolder)
            ? "未选择项目"
            : GetProjectFolderDisplayName();

        SetStatus($"空闲，已加载 {Workflows.Count} 个 workflow", Workflows.Count > 0 ? "Idle" : "Warning");
        AddLog($"刷新完成：{ProjectFolder}，当前显示 {Workflows.Count} 个 workflow，警告 {Warnings.Count} 条。");
        if (Workflows.Count > 0 && activateWorkflowView)
        {
            ActiveView = "Workflow";
            IsRecentExpanded = true;
        }

        return Workflows.Count;
    }

    private void AddWorkflowsFromFolder(string folder, string? sourceLabel)
    {
        WorkflowScanResult result = _workflowScanner.Scan(folder);
        foreach (WorkflowItem workflow in result.Workflows.OrderBy(item => item.ProjectName).ThenBy(item => item.WorkflowName))
        {
            Workflows.Add(workflow);
        }

        foreach (string warning in result.Warnings)
        {
            Warnings.Add(string.IsNullOrWhiteSpace(sourceLabel) ? warning : $"{sourceLabel}：{warning}");
        }
    }

    private Task RefreshSharedLibraryAsync(object? parameter)
    {
        if (IsRunning || string.IsNullOrWhiteSpace(SharedLibraryFolder)) return Task.CompletedTask;

        try
        {
            SharedLibraryStatus = "正在读取共享工作流目录…";
            LoadWorkflows(activateWorkflowView: false);
            int sharedProjectCount = RecentProjects.Count(item => item.IsSharedProject);
            if (sharedProjectCount > 0)
            {
                IsRecentExpanded = true;
            }
            SharedLibraryStatus = Directory.Exists(SharedLibraryFolder)
                ? $"已读取共享目录：发现 {sharedProjectCount} 个项目；点击左侧项目查看 workflow"
                : $"共享工作流目录不可访问：{SharedLibraryFolder}";
        }
        catch (Exception ex)
        {
            SharedLibraryStatus = "读取共享目录失败：" + ex.Message;
            AddLog(SharedLibraryStatus);
        }

        return Task.CompletedTask;
    }

    private async Task RunWorkflowAsync(object? parameter)
    {
        if (parameter is not WorkflowItem workflow)
        {
            return;
        }

        if (!workflow.CanRun)
        {
            SetStatus($"已阻止执行：{workflow.CompatibilityStatus}", "Warning");
            AddLog($"已阻止执行：{workflow.WorkflowName}，{workflow.CompatibilityDetails}");
            return;
        }

        IsRunning = true;
        _stopRequested = false;
        _activeWorkflow = workflow;
        workflow.IsExecuting = true;
        workflow.LastRunStatus = "执行中";
        SetStatus($"执行中：{workflow.WorkflowName}", "Running");
        AddLog($"开始执行：{workflow.WorkflowIdArgument}");

        try
        {
            MaxwellRuntimeRunResult runResult = await _runtimeRunner.RunAsync(workflow, _useBundledBrowser);
            if (_stopRequested)
            {
                workflow.LastRunStatus = "已停止";
                SetStatus($"已停止：{workflow.WorkflowName}", "Warning");
                AddLog($"已停止：{workflow.WorkflowName}");
            }
            else
            {
                workflow.LastRunStatus = "执行完成";
                SetStatus($"执行完成：{workflow.WorkflowName}", "Success");
                string outputSummary = runResult.OutputKeys.Count == 0
                    ? "无输出参数"
                    : "输出参数：" + string.Join("、", runResult.OutputKeys);
                AddLog($"执行完成：{workflow.WorkflowName}（Maxwell RuntimeHost，{outputSummary}）");
            }
        }
        catch (Exception ex)
        {
            if (_stopRequested)
            {
                workflow.LastRunStatus = "已停止";
                SetStatus($"已停止：{workflow.WorkflowName}", "Warning");
                AddLog($"已停止：{workflow.WorkflowName}");
                return;
            }

            workflow.LastRunStatus = "执行失败";
            SetStatus($"执行失败：{ex.Message}", "Error");
            AddLog($"执行失败：{workflow.WorkflowName}，{ex.Message}");
            System.Windows.MessageBox.Show(
                ex.Message,
                "执行失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            workflow.IsExecuting = false;
            if (ReferenceEquals(_activeWorkflow, workflow)) _activeWorkflow = null;
            IsRunning = false;
        }
    }

    private async Task RunFirstWorkflowAsync(object? parameter)
    {
        WorkflowItem? firstWorkflow = Workflows
            .OrderBy(item => item.ProjectName)
            .ThenBy(item => item.WorkflowName)
            .FirstOrDefault();
        if (firstWorkflow is null)
        {
            SetStatus("没有可运行的 workflow。", "Warning");
            return;
        }

        await RunWorkflowAsync(firstWorkflow);
    }

    private async Task StopWorkflowAsync(object? parameter)
    {
        SetStatus("正在停止当前 RuntimeHost...", "Warning");
        AddLog("正在停止当前 RuntimeHost。 ");
        _stopRequested = true;

        try
        {
            MaxwellRuntimeStopResult result = await _runtimeRunner.StopCurrentWorkflowAsync();
            if (result.WasRunning)
            {
                _stopRequested = true;
                SetStatus("已请求停止当前 workflow...", "Warning");
                AddLog("已终止当前 workflow 的 RuntimeHost；浏览器将保持打开。");
                return;
            }

            SetStatus("当前没有正在执行的 workflow。", "Warning");
            AddLog("停止请求未找到活动的 RuntimeHost 进程。");
            // Keep the stop request active while RunAsync unwinds. This covers the
            // race where RuntimeHost already exited but stdout/stderr is still read.
            if (IsRunning)
            {
                if (_activeWorkflow is not null)
                {
                    _activeWorkflow.IsExecuting = false;
                    _activeWorkflow.LastRunStatus = "已停止";
                }
                IsRunning = false;
                SetStatus("RuntimeHost 已结束，界面已恢复。", "Warning");
                AddLog("RuntimeHost 已结束，已解除执行状态。");
            }
            else
            {
                _stopRequested = false;
            }
        }
        catch (Exception ex)
        {
            _stopRequested = false;
            SetStatus($"停止失败：{ex.Message}", "Error");
            AddLog($"停止失败：{ex.Message}");
            System.Windows.MessageBox.Show(
                ex.Message,
                "停止失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void ShowRecentProjects()
    {
        SynchronizeRecentProjects();

        if (IsRecentExpanded && _isRecentHeaderSelected)
        {
            ActiveView = "Home";
            IsRecentExpanded = false;
            IsRecentSearchVisible = false;
            RecentProjectSearchText = null;
            SetRecentHeaderSelected(false);
            ClearRecentProjectSelection();
            return;
        }

        IsRecentExpanded = true;
        IsRecentSearchVisible = false;
        RecentProjectSearchText = null;
        ActiveView = "Home";
        SetRecentHeaderSelected(true);
        ClearRecentProjectSelection();
    }

    private void ToggleRecentProjectSearch()
    {
        if (!IsRecentExpanded)
        {
            IsRecentExpanded = true;
            ActiveView = "Home";
            SetRecentHeaderSelected(true);
            ClearRecentProjectSelection();
        }

        IsRecentSearchVisible = !IsRecentSearchVisible;
        if (!IsRecentSearchVisible)
        {
            RecentProjectSearchText = null;
        }
    }

    private void ShowSettings()
    {
        if (ActiveView == "Settings")
        {
            ActiveView = "Home";
            return;
        }

        ActiveView = "Settings";
        IsRecentExpanded = false;
        SetRecentHeaderSelected(false);
        ClearRecentProjectSelection();
    }

    private void SelectRecentProject(object? parameter)
    {
        if (parameter is not RecentProjectItem item)
        {
            return;
        }

        ProjectFolder = item.Path;
        IsRecentExpanded = true;
        SetRecentHeaderSelected(false);
        ClearRecentProjectSelection();
        ActiveView = "Workflow";
        LoadWorkflows();
        SelectRecentProjectByPath(item.Path);
    }

    private bool FilterWorkflow(object item)
    {
        if (item is not WorkflowItem workflow)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        string keyword = SearchText.Trim();
        return Contains(workflow.WorkflowName, keyword)
            || Contains(workflow.Filename, keyword)
            || Contains(workflow.ProjectName, keyword)
            || Contains(workflow.Id, keyword)
            || Contains(workflow.ProjectId, keyword);
    }

    private bool FilterRecentProject(object item)
    {
        if (item is not RecentProjectItem recentProject)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(RecentProjectSearchText))
        {
            return true;
        }

        string keyword = RecentProjectSearchText.Trim();
        return Contains(recentProject.Name, keyword)
            || Contains(recentProject.Path, keyword);
    }

    private static bool Contains(string value, string keyword)
    {
        return value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    private void AddLog(string message)
    {
        Logs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Logs.Count > 200)
        {
            Logs.RemoveAt(0);
        }
    }

    private void LoadRecentProjects()
    {
        RecentProjects.Clear();

        foreach (string sharedProjectPath in GetSharedProjectPaths())
        {
            RecentProjects.Add(new RecentProjectItem
            {
                Name = GetFolderDisplayName(sharedProjectPath),
                Path = sharedProjectPath,
                IsSharedProject = true,
                IsSelected = string.Equals(sharedProjectPath, ProjectFolder, StringComparison.OrdinalIgnoreCase)
                    && ActiveView == "Workflow"
            });
        }
    }

    private void SynchronizeRecentProjects()
    {
        List<string> sharedProjectPaths = GetSharedProjectPaths().ToList();
        bool currentProjectUnavailable = !string.IsNullOrWhiteSpace(ProjectFolder) &&
            !sharedProjectPaths.Any(path => string.Equals(path, ProjectFolder, StringComparison.OrdinalIgnoreCase));
        if (currentProjectUnavailable)
        {
            ProjectFolder = null;
            CurrentProjectName = "未选择项目";
            AddLog("当前项目不在已配置的共享工作流目录中，已从项目列表中移除。");
        }

        LoadRecentProjects();
    }

    private void SetRecentHeaderSelected(bool value)
    {
        if (_isRecentHeaderSelected == value)
        {
            return;
        }

        _isRecentHeaderSelected = value;
        OnPropertyChanged(nameof(IsRecentProjectsSelected));
    }

    private void ClearRecentProjectSelection()
    {
        foreach (RecentProjectItem item in RecentProjects)
        {
            item.IsSelected = false;
        }
    }

    private void SelectRecentProjectByPath(string path)
    {
        foreach (RecentProjectItem item in RecentProjects)
        {
            item.IsSelected = string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ClearProjectsForSharedLibraryChange()
    {
        Workflows.Clear();
        Warnings.Clear();
        RecentProjects.Clear();
        ProjectFolder = null;
        CurrentProjectName = "未选择项目";
        SearchText = null;
        OnPropertyChanged(nameof(WorkflowCount));
        OnPropertyChanged(nameof(VisibleWorkflowCount));
        RunFirstWorkflowCommand.RaiseCanExecuteChanged();
        ClearRecentProjectSelection();
    }

    private void SaveSettings()
    {
        _settings.SharedLibraryFolder = SharedLibraryFolder;
        _settings.RunHotkey = RunHotkey;
        _settings.StopHotkey = StopHotkey;
        _settingsService.Save(_settings);
    }

    private bool SetRunHotkey(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) &&
            string.Equals(key, StopHotkey, StringComparison.OrdinalIgnoreCase))
        {
            HotkeyError = "运行和停止不能使用相同快捷键。";
            System.Windows.MessageBox.Show(
                HotkeyError,
                "快捷键冲突",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        RunHotkey = key;
        HotkeyError = null;
        return true;
    }

    private bool SetStopHotkey(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) &&
            string.Equals(key, RunHotkey, StringComparison.OrdinalIgnoreCase))
        {
            HotkeyError = "运行和停止不能使用相同快捷键。";
            System.Windows.MessageBox.Show(
                HotkeyError,
                "快捷键冲突",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        StopHotkey = key;
        HotkeyError = null;
        return true;
    }

    private string GetProjectFolderDisplayName()
    {
        if (string.IsNullOrWhiteSpace(ProjectFolder))
        {
            return "未选择 Project";
        }

        return GetFolderDisplayName(ProjectFolder);
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        string? name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? folderPath : name;
    }

    private IEnumerable<string> GetSharedProjectPaths()
    {
        if (string.IsNullOrWhiteSpace(SharedLibraryFolder) || !Directory.Exists(SharedLibraryFolder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(SharedLibraryFolder)
                .OrderBy(GetFolderDisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AddLog($"读取共享项目列表失败：{ex.Message}");
            return [];
        }
    }

}
