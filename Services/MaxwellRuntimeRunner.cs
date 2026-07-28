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

    public async Task<MaxwellRuntimeRunResult> RunAsync(WorkflowItem workflow, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Process process;
        lock (_syncRoot)
        {
            if (_currentProcess is not null)
            {
                throw new InvalidOperationException("已有 workflow 正在执行。请先等待其完成或停止。");
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = ResolveRuntimeHostPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(workflow.SourceFile) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add(workflow.SourceFile);
            startInfo.ArgumentList.Add("--runtime-dir");
            startInfo.ArgumentList.Add(ResolveRuntimeDirectory());
            startInfo.ArgumentList.Add("--workflow-root");
            startInfo.ArgumentList.Add(ResolveWorkflowRoot(workflow));

            string? bundledChromeDirectory = BundledChromeLocator.FindBundledChromeDirectory();
            if (!string.IsNullOrWhiteSpace(bundledChromeDirectory))
            {
                string existingPath = startInfo.Environment.TryGetValue("PATH", out string? currentPath)
                    ? currentPath ?? string.Empty
                    : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                startInfo.Environment["PATH"] = bundledChromeDirectory + Path.PathSeparator + existingPath;
                startInfo.Environment["MAXWELL_BUNDLED_CHROME"] = Path.Combine(bundledChromeDirectory, "chrome.exe");
                startInfo.Environment["MAXWELL_BROWSER_PROFILE"] = BundledChromeLocator.MaxwellBrowserProfileDirectory;
            }

            string? bundledExtensionDirectory = BundledChromeLocator.FindBundledExtensionDirectory();
            if (!string.IsNullOrWhiteSpace(bundledExtensionDirectory))
            {
                startInfo.Environment["MAXWELL_BROWSER_EXTENSION"] = bundledExtensionDirectory;
                // Register before any workflow can launch the bundled browser.
                // This also covers simple Utilities.StartProcess workflows that
                // do not reference the OpenRPA.NM assembly directly.
                new ChromeAutomationStatusService().RegisterBundledNativeHost();
            }

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _currentProcess = process;
            _currentRunCancellation = runCancellation;
        }

        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动 Maxwell RuntimeHost。");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(runCancellation.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(runCancellation.Token);
            await process.WaitForExitAsync(runCancellation.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            RuntimeHostResponse? response = TryParseFinalResponse(stdout);
            if (response is null)
            {
                string output = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                throw new InvalidOperationException("Maxwell RuntimeHost 未返回有效结果。" +
                    (string.IsNullOrWhiteSpace(output) ? string.Empty : Environment.NewLine + output));
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
            process.Dispose();
            lock (_syncRoot)
            {
                if (ReferenceEquals(_currentProcess, process))
                {
                    _currentProcess = null;
                    _currentRunCancellation = null;
                }
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
