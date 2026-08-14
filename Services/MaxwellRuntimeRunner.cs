using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OpenRpaWorkflowLauncher.Models;

namespace OpenRpaWorkflowLauncher.Services;

public sealed class MaxwellRuntimeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly object _syncRoot = new();
    private Process? _currentProcess;
    private CancellationTokenSource? _currentRunCancellation;
    private bool _runReserved;

    public async Task<MaxwellRuntimeRunResult> RunAsync(
        WorkflowItem workflow,
        bool useBundledBrowser,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_syncRoot)
        {
            if (_runReserved)
            {
                throw new InvalidOperationException("已有 workflow 正在执行。请先等待其完成或停止。");
            }

            // Reserve the runner before any potentially slow network/runtime
            // preparation. Stop can then cancel a run that has not launched its
            // RuntimeHost yet, and a second invocation cannot race through setup.
            _runReserved = true;
            _currentRunCancellation = runCancellation;
        }

        Process? process = null;
        string resultFile = Path.Combine(Path.GetTempPath(), "maxwell-runtime-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = ResolveRuntimeHostPath(),
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(workflow.SourceFile) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(workflow.SourceFile);
            startInfo.ArgumentList.Add("--runtime-dir");
            startInfo.ArgumentList.Add(ResolveRuntimeDirectory());
            startInfo.ArgumentList.Add("--workflow-root");
            startInfo.ArgumentList.Add(ResolveWorkflowRoot(workflow));
            startInfo.ArgumentList.Add("--result-file");
            startInfo.ArgumentList.Add(resultFile);

            // Native Messaging is needed by both distribution variants. The
            // bundled-browser package ships a profile with the extension
            // already installed; the local-browser package relies on the
            // user's one-time installation of that same official extension.
            // In either case the executable and registry manifest must live
            // under the current Windows user's local profile, never on a UNC
            // share.
            string? nativeHostDirectory = BundledChromeLocator.EnsureLocalNativeMessagingHostDirectory();
            if (!string.IsNullOrWhiteSpace(nativeHostDirectory))
            {
                new ChromeAutomationStatusService().RegisterBundledNativeHost(nativeHostDirectory);
                startInfo.Environment["MAXWELL_NATIVE_HOST_DIRECTORY"] = nativeHostDirectory;
            }

            if (useBundledBrowser)
            {
                string? bundledChromeDirectory = BundledChromeLocator.EnsureLocalBundledChromeDirectory();
                if (!string.IsNullOrWhiteSpace(bundledChromeDirectory))
                {
                    string existingPath = startInfo.Environment.TryGetValue("PATH", out string? currentPath)
                        ? currentPath ?? string.Empty
                        : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    string launcherDirectory = Path.Combine(ResolveRuntimeDirectory(), "browser-launcher");
                    string launcherExecutable = Path.Combine(launcherDirectory, "chrome.exe");
                    // OpenRPA's StartProcess activity receives the workflow's original
                    // Filename="chrome". Put a tiny launcher ahead of Chromium on PATH
                    // so that command consistently receives Maxwell's isolated profile.
                    // This preserves the workflow XAML and avoids raw-XAML rewriting.
                    startInfo.Environment["PATH"] =
                        (File.Exists(launcherExecutable) ? launcherDirectory + Path.PathSeparator : string.Empty) +
                        bundledChromeDirectory + Path.PathSeparator + existingPath;
                    // Chrome++ owns the versioned chrome.dll engine and must
                    // remain the entry point. The isolated profile already
                    // contains the official OpenRPA extension. Do not also
                    // side-load Maxwell's private build: both use the same
                    // extension id, and Chrome disables the installed copy
                    // when the two sources collide, leaving Native Messaging
                    // with no active extension.
                    startInfo.Environment["MAXWELL_BUNDLED_CHROME"] = Path.Combine(bundledChromeDirectory, "chrome.exe");
                    startInfo.Environment["MAXWELL_CHROME_LAUNCHER"] = launcherExecutable;
                    startInfo.Environment["MAXWELL_BROWSER_PROFILE"] =
                        BundledChromeLocator.EnsureLocalBundledBrowserProfileDirectory();
                    // Enable only the Chromium/UIA compatibility path in the
                    // OpenRPA.Windows runtime. Desktop selectors designed for
                    // Excel, file dialogs and other applications retain their
                    // normal OpenRPA behavior.
                    startInfo.Environment["MAXWELL_WINDOWS_UIA_COMPATIBILITY"] = "1";
                    startInfo.Environment["MAXWELL_BROWSER_DIAGNOSTICS"] = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Maxwell",
                        "Diagnostics");
                }
            }

            runCancellation.Token.ThrowIfCancellationRequested();
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            lock (_syncRoot)
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException("无法启动 Maxwell RuntimeHost。");
                _currentProcess = process;
            }
            await process.WaitForExitAsync(runCancellation.Token);
            string resultJson = File.Exists(resultFile)
                ? await File.ReadAllTextAsync(resultFile, CancellationToken.None)
                : string.Empty;
            RuntimeHostResponse? response = TryParseFinalResponse(resultJson);
            if (response is null)
            {
                throw new InvalidOperationException("Maxwell RuntimeHost 已结束，但未返回有效结果。");
            }

            if (!response.success || !string.Equals(response.action, "completed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(BuildFailureMessage(workflow, response));
            }

            return new MaxwellRuntimeRunResult
            {
                OutputKeys = response.outputKeys ?? [],
                RequiredAssemblies = response.requiredAssemblies ?? []
            };
        }
        finally
        {
            process?.Dispose();
            try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }
            lock (_syncRoot)
            {
                if (ReferenceEquals(_currentProcess, process))
                {
                    _currentProcess = null;
                }
                if (ReferenceEquals(_currentRunCancellation, runCancellation))
                    _currentRunCancellation = null;
                _runReserved = false;
            }
        }
    }

    public Task<MaxwellRuntimeStopResult> StopCurrentWorkflowAsync()
    {
        Process? process;
        CancellationTokenSource? cancellation;
        lock (_syncRoot)
        {
            process = _currentProcess;
            cancellation = _currentRunCancellation;
        }

        bool wasRunning = false;
        try
        {
            if (process is not null)
            {
                wasRunning = !process.HasExited;
                // Stop only the RuntimeHost. Browser workflows may have launched or
                // attached to Chrome; killing the whole process tree would close a
                // browser the user expects to keep open after cancelling a workflow.
                if (wasRunning) process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The run task disposed the process between reading it and stopping it.
            wasRunning = false;
        }
        finally
        {
            // A process may exit before Stop is clicked while its redirected output
            // is still being awaited. Cancel that wait so the GUI can always recover.
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        return Task.FromResult(new MaxwellRuntimeStopResult { WasRunning = wasRunning });
    }

    private static RuntimeHostResponse? TryParseFinalResponse(string output)
    {
        string[] lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = lines.Length - 1; index >= 0; index--)
        {
            try { return JsonSerializer.Deserialize<RuntimeHostResponse>(lines[index], JsonOptions); }
            catch (JsonException) { }
        }
        return null;
    }

    private static string BuildFailureMessage(WorkflowItem workflow, RuntimeHostResponse response)
    {
        List<string> lines =
        [
            "工作流执行失败",
            "工作流: " + workflow.ProjectName + "/" + workflow.WorkflowName,
            "源文件: " + workflow.SourceFile,
            "错误代码: " + (response.errorCode ?? "unknown"),
            "错误信息: " + (response.error ?? "RuntimeHost 未提供错误信息。")
        ];

        if (!string.IsNullOrWhiteSpace(response.details))
        {
            lines.Add("详细诊断:");
            lines.Add(response.details);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string ResolveRuntimeHostPath()
    {
        string publishedPath = Path.Combine(AppContext.BaseDirectory, "runtime", "Maxwell.RuntimeHost.exe");
        if (File.Exists(publishedPath)) return publishedPath;

        string adjacentPath = Path.Combine(AppContext.BaseDirectory, "Maxwell.RuntimeHost.exe");
        if (File.Exists(adjacentPath)) return adjacentPath;

        string developmentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Maxwell.RuntimeHost", "bin", "Release", "net48", "Maxwell.RuntimeHost.exe"));
        if (File.Exists(developmentPath)) return developmentPath;

        throw new FileNotFoundException(
            "未找到 Maxwell.RuntimeHost.exe。请先构建 RuntimeHost 或重新发布 Maxwell。",
            publishedPath);
    }

    private static string ResolveRuntimeDirectory()
    {
        string publishedDirectory = Path.Combine(AppContext.BaseDirectory, "runtime");
        if (Directory.Exists(publishedDirectory)) return publishedDirectory;

        string developmentDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "runtime-staging"));
        if (Directory.Exists(developmentDirectory)) return developmentDirectory;

        return Path.GetDirectoryName(ResolveRuntimeHostPath())!;
    }

    private static string ResolveWorkflowRoot(WorkflowItem workflow)
    {
        // A project import is stored in <local-project-root>\<OpenRPA-project>.
        // Supplying the local-project root lets RuntimeHost resolve InvokeOpenRPA
        // references both by Project/Workflow and by project-relative filename.
        DirectoryInfo? directory = new(Path.GetDirectoryName(workflow.SourceFile)!);
        while (directory.Parent is not null &&
               string.Equals(directory.Name, workflow.ProjectName, StringComparison.OrdinalIgnoreCase) == false)
        {
            directory = directory.Parent;
        }

        return string.Equals(directory.Name, workflow.ProjectName, StringComparison.OrdinalIgnoreCase)
            ? directory.Parent!.FullName
            : Path.GetDirectoryName(workflow.SourceFile)!;
    }
}

public sealed class MaxwellRuntimeRunResult
{
    public IReadOnlyList<string> OutputKeys { get; init; } = [];
    public IReadOnlyList<string> RequiredAssemblies { get; init; } = [];
}

public sealed class MaxwellRuntimeStopResult
{
    public bool WasRunning { get; init; }
}

internal sealed class RuntimeHostResponse
{
    public bool success { get; set; }
    public string? action { get; set; }
    public string? errorCode { get; set; }
    public string? error { get; set; }
    public string? details { get; set; }
    public List<string>? outputKeys { get; set; }
    public List<string>? requiredAssemblies { get; set; }
}
