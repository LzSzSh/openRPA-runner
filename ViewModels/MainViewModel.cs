using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using OpenRpaWorkflowLauncher.Models;
using OpenRpaWorkflowLauncher.Services;

namespace OpenRpaWorkflowLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettingsService _settingsService = new();
    private readonly WorkflowScanner _workflowScanner = new();
    private readonly MaxwellRuntimeRunner _runtimeRunner = new();
    private readonly CompanyDeploymentSettingsService _companyDeploymentSettingsService = new();
    private readonly ChromeAutomationStatusService _chromeAutomationStatusService = new();
    private readonly AutomationScheduleService _automationScheduleService = new();
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
    private string _browserAutomationStatus = "未检查。浏览器流程执行前建议点击“检查”。";
    private WorkMode _workMode;
    private readonly DispatcherTimer _automationTimer;
    private string? _automationSearchText;
    private string? _projectPickerSearchText;
    private bool _isAutomationEditorVisible;
    private RecentProjectItem? _selectedAutomationProject;
    private string _automationScheduleType = "Weekly";
    private int _automationDayOfMonth = 1;
    private int _automationHour = 9;
    private int _automationMinute;
    private bool _monday = true;
    private bool _tuesday;
    private bool _wednesday;
    private bool _thursday;
    private bool _friday;
    private bool _saturday;
    private bool _sunday;
    private string? _automationValidationMessage;

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        _sharedLibraryFolder = _settings.SharedLibraryFolder
            ?? _companyDeploymentSettingsService.LoadSharedLibraryFolder();
        _runHotkey = _settings.RunHotkey;
        _stopHotkey = _settings.StopHotkey;
        _useBundledBrowser = _browserModePolicy.Availability != BrowserModeAvailability.LocalOnly;
        _workMode = Enum.TryParse(_settings.WorkMode, ignoreCase: true, out WorkMode savedMode)
            ? savedMode
            : WorkMode.Runtime;

        BrowseSharedLibraryCommand = new RelayCommand(_ => BrowseSharedLibraryFolder());
        SyncSharedLibraryCommand = new AsyncRelayCommand(RefreshSharedLibraryAsync, _ => !IsRunning && !string.IsNullOrWhiteSpace(SharedLibraryFolder));
        RefreshCommand = new RelayCommand(_ => LoadWorkflows(), _ => !IsRunning);
        RunFirstWorkflowCommand = new AsyncRelayCommand(RunFirstWorkflowAsync, _ => IsRuntimeMode && !IsRunning && Workflows.Count > 0);
        RunWorkflowCommand = new AsyncRelayCommand(RunWorkflowAsync, parameter => IsRuntimeMode && !IsRunning && parameter is WorkflowItem workflow && workflow.CanRun);
        StopWorkflowCommand = new AsyncRelayCommand(StopWorkflowAsync, _ => IsRunning);
        ClearLogsCommand = new RelayCommand(_ => Logs.Clear());
        ShowRecentProjectsCommand = new RelayCommand(_ => ShowRecentProjects());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        ShowAutomationsCommand = new RelayCommand(_ => ShowAutomations());
        ShowAutomationEditorCommand = new RelayCommand(_ => ShowAutomationEditor());
        CancelAutomationEditorCommand = new RelayCommand(_ => IsAutomationEditorVisible = false);
        AddAutomationCommand = new RelayCommand(_ => AddAutomation());
        DeleteAutomationCommand = new RelayCommand(DeleteAutomation);
        ClearRunHotkeyCommand = new RelayCommand(_ => SetRunHotkey(null));
        ClearStopHotkeyCommand = new RelayCommand(_ => SetStopHotkey(null));
        SelectRecentProjectCommand = new RelayCommand(SelectRecentProject);
        ToggleRecentProjectSearchCommand = new RelayCommand(_ => ToggleRecentProjectSearch());
        CheckBrowserAutomationCommand = new RelayCommand(_ => CheckBrowserAutomation());
        SwitchToRuntimeModeCommand = new RelayCommand(_ => SwitchWorkMode(WorkMode.Runtime), _ => !IsRunning && !IsRuntimeMode);
        SwitchToEditModeCommand = new RelayCommand(_ => SwitchWorkMode(WorkMode.Edit), _ => !IsRunning && !IsEditMode);
        WorkflowView = CollectionViewSource.GetDefaultView(Workflows);
        WorkflowView.Filter = FilterWorkflow;
        RecentProjectView = CollectionViewSource.GetDefaultView(RecentProjects);
        RecentProjectView.Filter = FilterRecentProject;
        AutomationView = CollectionViewSource.GetDefaultView(Automations);
        AutomationView.Filter = FilterAutomation;
        AutomationProjectView = CollectionViewSource.GetDefaultView(AutomationProjects);
        AutomationProjectView.Filter = FilterAutomationProject;
        foreach (ScheduledAutomation automation in _automationScheduleService.Load()) Automations.Add(automation);
        _automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _automationTimer.Tick += AutomationTimer_Tick;
        _automationTimer.Start();
        SynchronizeRecentProjects();
        LoadWorkflows();
        ApplyInitialWorkMode();
    }

    public ObservableCollection<WorkflowItem> Workflows { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<RecentProjectItem> RecentProjects { get; } = [];
    public ObservableCollection<ScheduledAutomation> Automations { get; } = [];
    public ObservableCollection<RecentProjectItem> AutomationProjects { get; } = [];
    public ICollectionView WorkflowView { get; }
    public ICollectionView RecentProjectView { get; }
    public ICollectionView AutomationView { get; }
    public ICollectionView AutomationProjectView { get; }

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
                OnPropertyChanged(nameof(IsAutomationsVisible));
                OnPropertyChanged(nameof(IsRecentProjectsSelected));
                OnPropertyChanged(nameof(IsSettingsSelected));
                OnPropertyChanged(nameof(IsAutomationsSelected));
            }
        }
    }

    public Visibility IsHomeVisible => ActiveView == "Home" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsWorkflowVisible => ActiveView == "Workflow" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsSettingsVisible => ActiveView == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsAutomationsVisible => ActiveView == "Automations" ? Visibility.Visible : Visibility.Collapsed;
    public bool IsRecentProjectsSelected => _isRecentHeaderSelected;
    public bool IsSettingsSelected => ActiveView == "Settings";
    public bool IsAutomationsSelected => ActiveView == "Automations";

    public string? AutomationSearchText
    {
        get => _automationSearchText;
        set
        {
            if (SetProperty(ref _automationSearchText, value)) AutomationView.Refresh();
        }
    }

    public string? ProjectPickerSearchText
    {
        get => _projectPickerSearchText;
        set
        {
            if (SetProperty(ref _projectPickerSearchText, value)) AutomationProjectView.Refresh();
        }
    }

    public bool IsAutomationEditorVisible
    {
        get => _isAutomationEditorVisible;
        private set => SetProperty(ref _isAutomationEditorVisible, value);
    }

    public RecentProjectItem? SelectedAutomationProject
    {
        get => _selectedAutomationProject;
        set => SetProperty(ref _selectedAutomationProject, value);
    }

    public bool IsWeeklySchedule
    {
        get => _automationScheduleType == "Weekly";
        set
        {
            if (value && _automationScheduleType != "Weekly")
            {
                _automationScheduleType = "Weekly";
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMonthlySchedule));
            }
        }
    }

    public bool IsMonthlySchedule
    {
        get => _automationScheduleType == "Monthly";
        set
        {
            if (value && _automationScheduleType != "Monthly")
            {
                _automationScheduleType = "Monthly";
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsWeeklySchedule));
            }
        }
    }

    public int AutomationDayOfMonth { get => _automationDayOfMonth; set => SetProperty(ref _automationDayOfMonth, value); }
    public int AutomationHour { get => _automationHour; set => SetProperty(ref _automationHour, value); }
    public int AutomationMinute { get => _automationMinute; set => SetProperty(ref _automationMinute, value); }
    public bool Monday { get => _monday; set => SetProperty(ref _monday, value); }
    public bool Tuesday { get => _tuesday; set => SetProperty(ref _tuesday, value); }
    public bool Wednesday { get => _wednesday; set => SetProperty(ref _wednesday, value); }
    public bool Thursday { get => _thursday; set => SetProperty(ref _thursday, value); }
    public bool Friday { get => _friday; set => SetProperty(ref _friday, value); }
    public bool Saturday { get => _saturday; set => SetProperty(ref _saturday, value); }
    public bool Sunday { get => _sunday; set => SetProperty(ref _sunday, value); }
    public string? AutomationValidationMessage
    {
        get => _automationValidationMessage;
        private set => SetProperty(ref _automationValidationMessage, value);
    }

    public IReadOnlyList<int> MonthDays { get; } = Enumerable.Range(1, 31).ToList();
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 60).ToList();

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

    public string BrowserAutomationStatus
    {
        get => _browserAutomationStatus;
        private set => SetProperty(ref _browserAutomationStatus, value);
    }

    public bool IsRuntimeMode => _workMode == WorkMode.Runtime;
    public bool IsEditMode => _workMode == WorkMode.Edit;
    public string WorkModeTitle => IsRuntimeMode ? "运行模式" : "编辑模式";
    public string WorkModeDescription => IsRuntimeMode
        ? "Maxwell 接管浏览器自动化，可以执行项目流程。"
        : "OpenRPA 接管浏览器自动化，用于录制、高亮和调试元素；Maxwell 不允许执行项目流程。";

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
                SwitchToRuntimeModeCommand.RaiseCanExecuteChanged();
                SwitchToEditModeCommand.RaiseCanExecuteChanged();
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
    public RelayCommand ShowAutomationsCommand { get; }
    public RelayCommand ShowAutomationEditorCommand { get; }
    public RelayCommand CancelAutomationEditorCommand { get; }
    public RelayCommand AddAutomationCommand { get; }
    public RelayCommand DeleteAutomationCommand { get; }
    public RelayCommand ClearRunHotkeyCommand { get; }
    public RelayCommand ClearStopHotkeyCommand { get; }
    public RelayCommand SelectRecentProjectCommand { get; }
    public RelayCommand ToggleRecentProjectSearchCommand { get; }
    public RelayCommand CheckBrowserAutomationCommand { get; }
    public RelayCommand SwitchToRuntimeModeCommand { get; }
    public RelayCommand SwitchToEditModeCommand { get; }

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

    private Task RunWorkflowAsync(object? parameter) => RunWorkflowAsync(parameter, showFailureDialog: true);

    private async Task RunWorkflowAsync(object? parameter, bool showFailureDialog)
    {
        if (!IsRuntimeMode)
        {
            SetStatus("当前是编辑模式，请切换到运行模式后再执行。", "Warning");
            AddLog("已阻止执行：当前处于编辑模式。");
            return;
        }

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
            if (showFailureDialog)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "执行失败",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
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
            // RuntimeHost may already have exited while RunAsync is still
            // unwinding setup/cancellation. Never force the visible state to idle
            // here: AsyncRelayCommand is still executing and doing so splits the
            // UI state from the real command state, leaving every run button
            // apparently idle but non-clickable. RunWorkflowAsync.finally is the
            // single owner that releases IsRunning and the active workflow.
            if (IsRunning)
            {
                SetStatus("正在结束当前 workflow...", "Warning");
                AddLog("RuntimeHost 已结束，正在等待执行任务完整退出。");
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

    private void ShowAutomations()
    {
        ActiveView = ActiveView == "Automations" ? "Home" : "Automations";
        IsRecentExpanded = false;
        SetRecentHeaderSelected(false);
        ClearRecentProjectSelection();
        if (ActiveView == "Automations") LoadAutomationProjects();
    }

    private void ShowAutomationEditor()
    {
        AutomationValidationMessage = null;
        LoadAutomationProjects();
        ProjectPickerSearchText = null;
        SelectedAutomationProject = null;
        IsAutomationEditorVisible = true;
    }

    private void LoadAutomationProjects()
    {
        AutomationProjects.Clear();
        AutomationValidationMessage = null;
        if (string.IsNullOrWhiteSpace(SharedLibraryFolder) || !Directory.Exists(SharedLibraryFolder))
        {
            AutomationValidationMessage = "请先在设置中配置可访问的网络共享工作流目录。";
            AutomationProjectView.Refresh();
            return;
        }

        try
        {
            foreach (string projectFolder in Directory.EnumerateDirectories(SharedLibraryFolder)
                         .OrderBy(GetFolderDisplayName, StringComparer.OrdinalIgnoreCase))
            {
                AutomationProjects.Add(new RecentProjectItem
                {
                    Name = GetFolderDisplayName(projectFolder),
                    Path = projectFolder,
                    IsSharedProject = true
                });
            }

            bool schedulesMigrated = false;
            foreach (ScheduledAutomation automation in Automations.Where(item => string.IsNullOrWhiteSpace(item.ProjectFolder)))
            {
                RecentProjectItem? project = AutomationProjects.FirstOrDefault(item =>
                    string.Equals(item.Name, automation.ProjectName, StringComparison.OrdinalIgnoreCase));
                if (project is null) continue;
                automation.ProjectFolder = project.Path;
                schedulesMigrated = true;
            }
            if (schedulesMigrated) _automationScheduleService.Save(Automations);
        }
        catch (Exception ex)
        {
            AutomationValidationMessage = "读取网络项目失败：" + ex.Message;
        }

        AutomationProjectView.Refresh();
    }

    private void AddAutomation()
    {
        AutomationValidationMessage = null;
        if (SelectedAutomationProject is null)
        {
            AutomationValidationMessage = "请先搜索并选择一个项目。";
            return;
        }

        WorkflowItem? firstWorkflow = GetFirstProjectWorkflow(SelectedAutomationProject.Path);
        if (firstWorkflow is null)
        {
            AutomationValidationMessage = "该项目中没有可识别的工作流。";
            return;
        }

        if (!firstWorkflow.CanRun)
        {
            AutomationValidationMessage = "该项目的第一个工作流当前不可执行：" + firstWorkflow.CompatibilityDetails;
            return;
        }

        List<DayOfWeek> weekdays = GetSelectedWeekdays();
        if (IsWeeklySchedule && weekdays.Count == 0)
        {
            AutomationValidationMessage = "每周执行至少需要选择一个星期。";
            return;
        }

        ScheduledAutomation automation = new()
        {
            ProjectName = SelectedAutomationProject.Name,
            ProjectFolder = SelectedAutomationProject.Path,
            ScheduleType = IsMonthlySchedule ? "Monthly" : "Weekly",
            Weekdays = IsWeeklySchedule ? weekdays : [],
            DayOfMonth = IsMonthlySchedule ? AutomationDayOfMonth : null,
            Hour = AutomationHour,
            Minute = AutomationMinute
        };
        Automations.Add(automation);
        _automationScheduleService.Save(Automations);
        AutomationView.Refresh();
        IsAutomationEditorVisible = false;
        AddLog($"已添加定时任务：{automation.ProjectDisplay}，{automation.ScheduleText}");
    }

    private void DeleteAutomation(object? parameter)
    {
        if (parameter is not ScheduledAutomation automation) return;

        MessageBoxResult result = MessageBox.Show(
            $"确定删除定时任务“{automation.ProjectDisplay}”吗？",
            "删除定时任务",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        Automations.Remove(automation);
        _automationScheduleService.Save(Automations);
        AutomationView.Refresh();
        AddLog($"已删除定时任务：{automation.ProjectDisplay}");
    }

    private List<DayOfWeek> GetSelectedWeekdays()
    {
        List<DayOfWeek> result = [];
        if (Monday) result.Add(DayOfWeek.Monday);
        if (Tuesday) result.Add(DayOfWeek.Tuesday);
        if (Wednesday) result.Add(DayOfWeek.Wednesday);
        if (Thursday) result.Add(DayOfWeek.Thursday);
        if (Friday) result.Add(DayOfWeek.Friday);
        if (Saturday) result.Add(DayOfWeek.Saturday);
        if (Sunday) result.Add(DayOfWeek.Sunday);
        return result;
    }

    private bool FilterAutomation(object item)
    {
        if (item is not ScheduledAutomation automation) return false;
        if (string.IsNullOrWhiteSpace(AutomationSearchText)) return true;
        string keyword = AutomationSearchText.Trim();
        return Contains(automation.ProjectName, keyword)
            || Contains(automation.ScheduleText, keyword);
    }

    private bool FilterAutomationProject(object item)
    {
        if (item is not RecentProjectItem project) return false;
        if (string.IsNullOrWhiteSpace(ProjectPickerSearchText)) return true;
        return Contains(project.Name, ProjectPickerSearchText.Trim());
    }

    private WorkflowItem? GetFirstProjectWorkflow(string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder) || !Directory.Exists(projectFolder)) return null;
        return _workflowScanner.Scan(projectFolder).Workflows
            .OrderBy(item => item.WorkflowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Filename, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private async void AutomationTimer_Tick(object? sender, EventArgs e)
    {
        if (IsRunning || !IsRuntimeMode) return;

        DateTime now = DateTime.Now;
        ScheduledAutomation? automation = Automations.FirstOrDefault(item =>
            item.IsDue(now, TimeSpan.FromMinutes(10)));
        if (automation is null) return;

        if (AutomationProjects.Count == 0) LoadAutomationProjects();
        if (string.IsNullOrWhiteSpace(automation.ProjectFolder))
        {
            automation.ProjectFolder = AutomationProjects.FirstOrDefault(item =>
                string.Equals(item.Name, automation.ProjectName, StringComparison.OrdinalIgnoreCase))?.Path ?? string.Empty;
        }

        WorkflowItem? workflow = GetFirstProjectWorkflow(automation.ProjectFolder);
        automation.LastRunAt = now;
        if (workflow is null)
        {
            automation.LastRunStatus = "项目不可访问或没有工作流";
            _automationScheduleService.Save(Automations);
            AddLog($"定时任务未执行：{automation.ProjectDisplay} 不可访问或没有工作流。");
            return;
        }

        automation.LastRunStatus = "执行中";
        _automationScheduleService.Save(Automations);
        AddLog($"定时任务触发：{automation.ProjectDisplay}，自动执行第一个工作流 {workflow.WorkflowName}");
        await RunWorkflowAsync(workflow, showFailureDialog: false);
        automation.LastRunStatus = workflow.LastRunStatus;
        _automationScheduleService.Save(Automations);
    }

    public void Dispose()
    {
        _automationTimer.Stop();
        _automationTimer.Tick -= AutomationTimer_Tick;
    }

    private void CheckBrowserAutomation()
    {
        BrowserAutomationCheckResult result = IsRuntimeMode
            ? _chromeAutomationStatusService.CheckAndPrepare(_useBundledBrowser)
            : _chromeAutomationStatusService.SwitchToEditMode();
        BrowserAutomationStatus = result.Message;
        AddLog("浏览器扩展检查：" + result.Message);
    }

    private void ApplyInitialWorkMode()
    {
        BrowserAutomationCheckResult result = IsRuntimeMode
            ? _chromeAutomationStatusService.SwitchToRuntimeMode(_useBundledBrowser)
            : _chromeAutomationStatusService.SwitchToEditMode();
        BrowserAutomationStatus = result.Message;

        // The run commands are intentionally disabled in edit mode. Make that
        // state visible on the workflow page instead of leaving an ambiguous
        // "idle" status that looks like a broken button.
        if (IsEditMode)
        {
            SetStatus("编辑模式：运行和执行已禁用，请在设置中切换到运行模式。", "Warning");
        }
    }

    private void SwitchWorkMode(WorkMode mode)
    {
        BrowserAutomationCheckResult result = mode == WorkMode.Runtime
            ? _chromeAutomationStatusService.SwitchToRuntimeMode(_useBundledBrowser)
            : _chromeAutomationStatusService.SwitchToEditMode();

        BrowserAutomationStatus = result.Message;
        AddLog("工作模式切换：" + result.Message);
        if (!result.IsReady)
        {
            System.Windows.MessageBox.Show(
                result.Message,
                "工作模式切换失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        _workMode = mode;
        OnPropertyChanged(nameof(IsRuntimeMode));
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(WorkModeTitle));
        OnPropertyChanged(nameof(WorkModeDescription));
        RunFirstWorkflowCommand.RaiseCanExecuteChanged();
        RunWorkflowCommand.RaiseCanExecuteChanged();
        SwitchToRuntimeModeCommand.RaiseCanExecuteChanged();
        SwitchToEditModeCommand.RaiseCanExecuteChanged();
        SaveSettings();
        SetStatus($"已切换到{WorkModeTitle}", "Idle");
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
        AutomationProjects.Clear();
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
        _settings.WorkMode = _workMode.ToString();
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
