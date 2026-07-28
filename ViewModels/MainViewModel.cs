using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using OpenRpaWorkflowLauncher.Models;
using OpenRpaWorkflowLauncher.Services;
using WinForms = System.Windows.Forms;

namespace OpenRpaWorkflowLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxStoredRecentProjects = 100;
    private readonly AppSettingsService _settingsService = new();
    private readonly WorkflowScanner _workflowScanner = new();
    private readonly LocalWorkflowImportService _workflowImportService = new();
    private readonly LocalProjectImportService _projectImportService = new();
    private readonly MaxwellRuntimeRunner _runtimeRunner = new();
    private readonly ChromeAutomationStatusService _chromeAutomationStatusService = new();
    private readonly MaxwellBrowserBootstrapper _browserBootstrapper = new();
    private readonly AppSettings _settings;

    private string? _projectFolder;
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
    private bool _isImporting;
    private bool _stopRequested;
    private WorkflowItem? _activeWorkflow;
    private bool _isRecentExpanded;
    private bool _isRecentSearchVisible;
    private bool _isRecentHeaderSelected;
    private ChromeAutomationStatus _chromeAutomationStatus = new();
    private string? _chromeAutomationMessage;

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        _projectFolder = _settings.LastProjectFolder;
        _runHotkey = _settings.RunHotkey;
        _stopHotkey = _settings.StopHotkey;

        BrowseProjectCommand = new RelayCommand(_ => BrowseProjectFolder());
        OpenProjectFolderCommand = new RelayCommand(_ => OpenProjectFolder(), _ => !string.IsNullOrWhiteSpace(ProjectFolder) && Directory.Exists(ProjectFolder));
        RefreshCommand = new RelayCommand(_ => LoadWorkflows(), _ => !IsRunning && !IsImporting);
        ImportWorkflowCommand = new AsyncRelayCommand(_ => BrowseAndImportAsync(), _ => !IsRunning && !IsImporting);
        RunFirstWorkflowCommand = new AsyncRelayCommand(RunFirstWorkflowAsync, _ => !IsRunning && !IsImporting && Workflows.Count > 0);
        RunWorkflowCommand = new AsyncRelayCommand(RunWorkflowAsync, parameter => !IsRunning && !IsImporting && parameter is WorkflowItem workflow && workflow.CanRun);
        StopWorkflowCommand = new AsyncRelayCommand(StopWorkflowAsync, _ => IsRunning);
        ClearLogsCommand = new RelayCommand(_ => Logs.Clear());
        ShowAddProjectCommand = new RelayCommand(_ => ShowAddProject());
        ShowRecentProjectsCommand = new RelayCommand(_ => ShowRecentProjects());
        ShowSettingsCommand = new RelayCommand(_ => ShowSettings());
        RefreshChromeAutomationCommand = new RelayCommand(_ => RefreshChromeAutomationStatus());
        RepairChromeNativeHostCommand = new RelayCommand(_ => RepairChromeNativeHost());
        OpenChromeExtensionStoreCommand = new RelayCommand(_ => OpenChromeExtensionStore());
        ClearRunHotkeyCommand = new RelayCommand(_ => SetRunHotkey(null));
        ClearStopHotkeyCommand = new RelayCommand(_ => SetStopHotkey(null));
        SelectRecentProjectCommand = new RelayCommand(SelectRecentProject);
        ToggleRecentProjectSearchCommand = new RelayCommand(_ => ToggleRecentProjectSearch());
        WorkflowView = CollectionViewSource.GetDefaultView(Workflows);
        WorkflowView.Filter = FilterWorkflow;
        RecentProjectView = CollectionViewSource.GetDefaultView(RecentProjects);
        RecentProjectView.Filter = FilterRecentProject;
        SynchronizeRecentProjects();
        RefreshChromeAutomationStatus();
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
        set
        {
            if (SetProperty(ref _projectFolder, value))
            {
                SaveSettings();
                OpenProjectFolderCommand.RaiseCanExecuteChanged();
            }
        }
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
                OnPropertyChanged(nameof(IsAddProjectVisible));
                OnPropertyChanged(nameof(IsWorkflowVisible));
                OnPropertyChanged(nameof(IsSettingsVisible));
                OnPropertyChanged(nameof(IsAddProjectSelected));
                OnPropertyChanged(nameof(IsRecentProjectsSelected));
                OnPropertyChanged(nameof(IsSettingsSelected));
            }
        }
    }

    public Visibility IsHomeVisible => ActiveView == "Home" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsAddProjectVisible => ActiveView == "AddProject" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsWorkflowVisible => ActiveView == "Workflow" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsSettingsVisible => ActiveView == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    public bool IsAddProjectSelected => ActiveView == "AddProject";
    public bool IsRecentProjectsSelected => _isRecentHeaderSelected;
    public bool IsSettingsSelected => ActiveView == "Settings";

    public ChromeAutomationStatus ChromeAutomationStatus
    {
        get => _chromeAutomationStatus;
        private set => SetProperty(ref _chromeAutomationStatus, value);
    }

    public string? ChromeAutomationMessage
    {
        get => _chromeAutomationMessage;
        private set => SetProperty(ref _chromeAutomationMessage, value);
    }

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
                StopWorkflowCommand.RaiseCanExecuteChanged();
                ImportWorkflowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                ImportWorkflowCommand.RaiseCanExecuteChanged();
                RunFirstWorkflowCommand.RaiseCanExecuteChanged();
                RunWorkflowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand BrowseProjectCommand { get; }
    public RelayCommand OpenProjectFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ImportWorkflowCommand { get; }
    public AsyncRelayCommand RunFirstWorkflowCommand { get; }
    public AsyncRelayCommand RunWorkflowCommand { get; }
    public AsyncRelayCommand StopWorkflowCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand ShowAddProjectCommand { get; }
    public RelayCommand ShowRecentProjectsCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ClearRunHotkeyCommand { get; }
    public RelayCommand ClearStopHotkeyCommand { get; }
    public RelayCommand SelectRecentProjectCommand { get; }
    public RelayCommand ToggleRecentProjectSearchCommand { get; }
    public RelayCommand RefreshChromeAutomationCommand { get; }
    public RelayCommand RepairChromeNativeHostCommand { get; }
    public RelayCommand OpenChromeExtensionStoreCommand { get; }

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

    public async Task ImportJsonFilesAsync(IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(ProjectFolder))
        {
            SetStatus("请先选择本地项目文件夹。", "Warning");
            AddLog("导入失败：未选择本地项目文件夹。");
            return;
        }

        Directory.CreateDirectory(ProjectFolder);
        List<string> importFiles = [];
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                ProjectFolder = path;
                LoadWorkflows();
                return;
            }

            if (File.Exists(path) &&
                (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetExtension(path), ".rpaproj", StringComparison.OrdinalIgnoreCase)))
            {
                importFiles.Add(path);
            }
        }

        if (importFiles.Count == 0)
        {
            SetStatus("没有找到可导入的 workflow JSON 或 .rpaproj 文件。", "Warning");
            AddLog("导入失败：未识别到 workflow JSON 或 .rpaproj 文件。");
            return;
        }

        IsImporting = true;
        int importedWorkflows = 0;
        int importedProjects = 0;
        List<string> failures = [];
        try
        {
            foreach (string importFile in importFiles)
            {
                SetStatus($"正在导入：{Path.GetFileName(importFile)}", "Running");
                try
                {
                    if (string.Equals(Path.GetExtension(importFile), ".rpaproj", StringComparison.OrdinalIgnoreCase))
                    {
                        LocalProjectImportResult response = await Task.Run(() =>
                            _projectImportService.Import(importFile, ProjectFolder!));
                        importedProjects++;
                        importedWorkflows += response.WorkflowCount;
                        AddLog($"已导入 OpenRPA Project：{response.ProjectName}，workflow {response.WorkflowCount} 个，依赖 {response.DependencyCount} 个 -> {response.TargetDirectory}");
                    }
                    else
                    {
                        LocalWorkflowImportResult response = await Task.Run(() =>
                            _workflowImportService.Import(importFile, ProjectFolder!));
                        importedWorkflows++;
                        AddLog($"已导入本地 workflow：{response.ProjectName}/{response.WorkflowName} -> {response.TargetPath}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(importFile)}：{ex.Message}");
                    AddLog($"导入失败：{Path.GetFileName(importFile)}，{ex.Message}");
                }
            }
        }
        finally
        {
            IsImporting = false;
        }

        LoadWorkflows();
        ActiveView = "Workflow";
        if (failures.Count == 0)
        {
            string summary = importedProjects > 0
                ? $"已导入 {importedProjects} 个 Project、{importedWorkflows} 个 workflow。"
                : $"已导入 {importedWorkflows} 个 workflow。";
            SetStatus(summary, "Success");
            return;
        }

        foreach (string failure in failures)
        {
            Warnings.Add(failure);
        }

        SetStatus(importedWorkflows > 0 || importedProjects > 0
            ? $"已导入 {importedProjects} 个 Project、{importedWorkflows} 个 workflow，另有 {failures.Count} 个失败。"
            : "导入失败。", "Warning");
        System.Windows.MessageBox.Show(string.Join(Environment.NewLine, failures), "导入失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    public Task ImportJsonFromClipboardTextAsync(string text)
    {
        string[] paths = text
            .Split([Environment.NewLine, "\r", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ImportJsonFilesAsync(paths);
    }

    private async Task BrowseAndImportAsync()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "选择 OpenRPA workflow JSON 或 Project",
            Filter = "OpenRPA 文件 (*.json;*.rpaproj)|*.json;*.rpaproj|Workflow JSON (*.json)|*.json|OpenRPA Project (*.rpaproj)|*.rpaproj|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            await ImportJsonFilesAsync(dialog.FileNames);
        }
    }

    private void BrowseProjectFolder()
    {
        using WinForms.FolderBrowserDialog dialog = new()
        {
            Description = "选择 Maxwell 本地项目文件夹",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(ProjectFolder) ? ProjectFolder : string.Empty
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            ProjectFolder = dialog.SelectedPath;
            LoadWorkflows();
        }
    }

    private void OpenProjectFolder()
    {
        if (string.IsNullOrWhiteSpace(ProjectFolder) || !Directory.Exists(ProjectFolder))
        {
            SetStatus("本地项目文件夹不存在。", "Warning");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = ProjectFolder,
            UseShellExecute = true
        });
    }

    private void LoadWorkflows()
    {
        SynchronizeRecentProjects();
        Workflows.Clear();
        Warnings.Clear();
        OnPropertyChanged(nameof(WorkflowCount));

        if (!string.IsNullOrWhiteSpace(ProjectFolder) && Directory.Exists(ProjectFolder))
        {
            WorkflowScanResult result = _workflowScanner.Scan(ProjectFolder);
            foreach (WorkflowItem workflow in result.Workflows.OrderBy(item => item.ProjectName).ThenBy(item => item.WorkflowName))
            {
                Workflows.Add(workflow);
            }

            foreach (string warning in result.Warnings)
            {
                Warnings.Add(warning);
            }
        }

        WorkflowView.Refresh();
        OnPropertyChanged(nameof(WorkflowCount));
        OnPropertyChanged(nameof(VisibleWorkflowCount));
        RunFirstWorkflowCommand.RaiseCanExecuteChanged();

        CurrentProjectName = Workflows.Count == 0 && string.IsNullOrWhiteSpace(ProjectFolder)
            ? "未选择本地项目"
            : GetProjectFolderDisplayName();

        SetStatus($"空闲，已加载 {Workflows.Count} 个 workflow", Workflows.Count > 0 ? "Idle" : "Warning");
        AddLog($"刷新完成：{ProjectFolder}，当前显示 {Workflows.Count} 个 workflow，警告 {Warnings.Count} 条。");
        AddRecentProject();
        if (Workflows.Count > 0)
        {
            ActiveView = "Workflow";
            IsRecentExpanded = true;
        }
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
        PromoteRecentProject();
        workflow.IsExecuting = true;
        workflow.LastRunStatus = "执行中";
        SetStatus($"执行中：{workflow.WorkflowName}", "Running");
        AddLog($"开始执行：{workflow.WorkflowIdArgument}");

        try
        {
            MaxwellRuntimeRunResult runResult = await _runtimeRunner.RunAsync(workflow);
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

    private void ShowAddProject()
    {
        if (ActiveView == "AddProject")
        {
            ActiveView = "Home";
            IsRecentExpanded = false;
            SetRecentHeaderSelected(false);
            ClearRecentProjectSelection();
            return;
        }

        ActiveView = "AddProject";
        IsRecentExpanded = false;
        SetRecentHeaderSelected(false);
        ClearRecentProjectSelection();
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

        RefreshChromeAutomationStatus();
        ActiveView = "Settings";
        IsRecentExpanded = false;
        SetRecentHeaderSelected(false);
        ClearRecentProjectSelection();
    }

    private void RefreshChromeAutomationStatus()
    {
        try
        {
            ChromeAutomationStatus = _chromeAutomationStatusService.Inspect();
            ChromeAutomationMessage = null;
        }
        catch (Exception ex)
        {
            ChromeAutomationMessage = "Chrome 状态检查失败：" + ex.Message;
        }
    }

    private void RepairChromeNativeHost()
    {
        try
        {
            _chromeAutomationStatusService.RegisterBundledNativeHost();
            RefreshChromeAutomationStatus();
            ChromeAutomationMessage = "Native Messaging Host 已注册。请完全退出并重新打开 Chrome，再点击“重新检查”。";
        }
        catch (Exception ex)
        {
            ChromeAutomationMessage = "修复 Native Messaging Host 失败：" + ex.Message;
        }
    }

    private void OpenChromeExtensionStore()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(BundledChromeLocator.FindBundledExtensionDirectory()))
            {
                _browserBootstrapper.LaunchExtensionManagementPage();
                ChromeAutomationMessage = "已启动 Maxwell 内置浏览器，并在独立资料目录中加载了包内 OpenRPA 浏览器扩展。请在扩展页确认该扩展已启用。";
                return;
            }

            const string extensionSearchUrl = "https://chromewebstore.google.com/search/OpenRPA";
            string? bundledChrome = BundledChromeLocator.FindBundledChromeExecutable();
            if (!string.IsNullOrWhiteSpace(bundledChrome) && File.Exists(bundledChrome))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = bundledChrome,
                    Arguments = extensionSearchUrl,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(bundledChrome)
                });
                ChromeAutomationMessage = "已通过内置 Chrome 打开 OpenRPA 扩展搜索页。安装并启用扩展后，请完全退出并重新打开该内置浏览器。";
                return;
            }

            Process.Start(new ProcessStartInfo(extensionSearchUrl) { UseShellExecute = true });
            ChromeAutomationMessage = "已打开 Chrome 网上应用店的 OpenRPA 搜索页。安装并启用扩展后，请完全退出并重新打开 Chrome。";
        }
        catch (Exception ex)
        {
            ChromeAutomationMessage = "无法打开 Chrome 扩展页：" + ex.Message;
        }
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
        foreach (RecentProjectInfo item in _settings.RecentProjects.Where(item => Directory.Exists(item.Path)))
        {
            RecentProjects.Add(new RecentProjectItem
            {
                Name = GetFolderDisplayName(item.Path),
                Path = item.Path,
                IsSelected = string.Equals(item.Path, ProjectFolder, StringComparison.OrdinalIgnoreCase)
                    && ActiveView == "Workflow"
            });
        }
    }

    private void SynchronizeRecentProjects()
    {
        List<RecentProjectInfo> synchronized = [];
        HashSet<string> knownPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (RecentProjectInfo item in _settings.RecentProjects)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !Directory.Exists(item.Path) || !knownPaths.Add(item.Path))
            {
                continue;
            }

            synchronized.Add(new RecentProjectInfo
            {
                Name = GetFolderDisplayName(item.Path),
                Path = item.Path
            });
        }

        bool recentProjectsChanged = !RecentProjectsMatch(_settings.RecentProjects, synchronized);
        if (recentProjectsChanged)
        {
            _settings.RecentProjects = synchronized;
        }

        bool currentProjectDeleted = !string.IsNullOrWhiteSpace(ProjectFolder) && !Directory.Exists(ProjectFolder);
        if (currentProjectDeleted)
        {
            ProjectFolder = null;
            CurrentProjectName = "未选择本地项目";
            AddLog("当前本地项目文件夹已被删除，已从最近项目中移除。");
        }
        else if (recentProjectsChanged)
        {
            SaveSettings();
        }

        LoadRecentProjects();
    }

    private static bool RecentProjectsMatch(IReadOnlyList<RecentProjectInfo> left, IReadOnlyList<RecentProjectInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Path, right[index].Path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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

    private void AddRecentProject()
    {
        UpdateRecentProject(moveToTop: false);
    }

    private void PromoteRecentProject()
    {
        UpdateRecentProject(moveToTop: true);
    }

    private void UpdateRecentProject(bool moveToTop)
    {
        if (string.IsNullOrWhiteSpace(ProjectFolder) || !Directory.Exists(ProjectFolder))
        {
            return;
        }

        string name = GetProjectFolderDisplayName();
        RecentProjectInfo? existing = _settings.RecentProjects.FirstOrDefault(item =>
            string.Equals(item.Path, ProjectFolder, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && !moveToTop)
        {
            return;
        }

        List<RecentProjectInfo> recent = _settings.RecentProjects
            .Where(item => !string.Equals(item.Path, ProjectFolder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        recent.Insert(0, new RecentProjectInfo { Name = name, Path = ProjectFolder });
        _settings.RecentProjects = recent.Take(MaxStoredRecentProjects).ToList();
        SaveSettings();
        LoadRecentProjects();
    }

    private void SaveSettings()
    {
        _settings.LastProjectFolder = ProjectFolder;
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
}
