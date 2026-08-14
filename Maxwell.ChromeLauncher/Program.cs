using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace Maxwell.ChromeLauncher
{
    internal static class Program
    {
        private const int BrowserReadyTimeoutMilliseconds = 15000;

        private static int Main(string[] args)
        {
            string chrome = Environment.GetEnvironmentVariable("MAXWELL_BUNDLED_CHROME");
            string profile = Environment.GetEnvironmentVariable("MAXWELL_BROWSER_PROFILE");
            if (string.IsNullOrWhiteSpace(chrome) || !File.Exists(chrome))
            {
                Console.Error.WriteLine("Maxwell bundled Chrome was not prepared.");
                return 2;
            }

            string prefix = "--no-first-run --no-default-browser-check --disable-default-apps --allow-file-access-from-files --force-renderer-accessibility";
            if (!string.IsNullOrWhiteSpace(profile)) prefix += " --user-data-dir=" + Quote(profile) + " --profile-directory=Default";
            string extension = Environment.GetEnvironmentVariable("MAXWELL_OPENRPA_EXTENSION");
            if (!string.IsNullOrWhiteSpace(extension) && Directory.Exists(extension))
            {
                prefix += " --disable-extensions-except=" + Quote(extension) + " --load-extension=" + Quote(extension);
            }
            string forwardedArguments = string.Join(" ", args.Select(Quote));
            string arguments = string.IsNullOrWhiteSpace(forwardedArguments) ? prefix : prefix + " " + forwardedArguments;
            string diagnosticsDirectory = Environment.GetEnvironmentVariable("MAXWELL_BROWSER_DIAGNOSTICS");
            using (BrowserDiagnostics diagnostics = new BrowserDiagnostics(diagnosticsDirectory))
            {
                diagnostics.Write("launch-path=" + chrome);
                diagnostics.Write("launch-arguments=" + arguments);
                using (Process launchProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = chrome,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(chrome),
                    // Chrome outlives this launcher. Shell execution prevents
                    // it from inheriting the RuntimeHost result pipes, which
                    // would otherwise keep Maxwell stuck in "running".
                    UseShellExecute = true
                }))
                {
                    diagnostics.Write("starter-pid=" + (launchProcess == null ? 0 : launchProcess.Id));
                }

                BrowserReadyResult ready = WaitForBrowserReady(chrome, profile, diagnostics);
                if (!ready.IsReady)
                {
                    Console.Error.WriteLine("MAXWELL_BROWSER_NOT_READY " + ready.Message);
                    diagnostics.Write("result=timeout; " + ready.Message + "; process-races=" + ready.ProcessRaceCount +
                        (ready.LastUiaExceptionHResult.HasValue ? "; last-uia-hresult=0x" + ready.LastUiaExceptionHResult.Value.ToString("X8") : string.Empty));
                    return 3;
                }

                diagnostics.Write("result=ready; pid=" + ready.ProcessId +
                    "; window-ms=" + ready.WindowMilliseconds +
                    "; document-ms=" + ready.DocumentMilliseconds +
                    "; process-races=" + ready.ProcessRaceCount);
                Console.WriteLine("MAXWELL_BROWSER_READY pid=" + ready.ProcessId +
                    " window-ms=" + ready.WindowMilliseconds +
                    " document-ms=" + ready.DocumentMilliseconds +
                    " process-races=" + ready.ProcessRaceCount);
                return 0;
            }
        }

        private static BrowserReadyResult WaitForBrowserReady(string chromePath, string profile, BrowserDiagnostics diagnostics)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            int processId = 0;
            long windowMilliseconds = -1;
            int processRaceCount = 0;
            int? lastUiaExceptionHResult = null;
            string lastReason = "no matching Chrome browser process";

            while (stopwatch.ElapsedMilliseconds < BrowserReadyTimeoutMilliseconds)
            {
                ChromeProcessDiscovery discovery = FindMaxwellChromeProcess(chromePath, profile, diagnostics);
                processRaceCount += discovery.ProcessRaceCount;
                ChromeProcessInfo process = discovery.BrowserProcess;

                if (process == null)
                {
                    lastReason = discovery.BrowserCandidates > 0
                        ? "matching Chrome browser process has no readable main window"
                        : discovery.ChildCandidates > 0
                            ? "only matching Chrome child processes were found"
                            : "no matching Chrome browser process was found";
                    Thread.Sleep(100);
                    continue;
                }

                processId = process.ProcessId;
                diagnostics.WriteOnce("browser-main-" + processId,
                    "browser-main pid=" + processId + "; visible=" + process.IsVisible +
                    "; handle=0x" + process.MainWindowHandle.ToInt64().ToString("X") +
                    "; command-line=" + process.CommandLine);

                if (process.MainWindowHandle == IntPtr.Zero || !process.IsVisible)
                {
                    lastReason = "matching Chrome browser process has no visible main window";
                    Thread.Sleep(100);
                    continue;
                }

                if (windowMilliseconds < 0)
                {
                    windowMilliseconds = stopwatch.ElapsedMilliseconds;
                    diagnostics.Write("main-window pid=" + processId + "; handle=0x" + process.MainWindowHandle.ToInt64().ToString("X") + "; elapsed-ms=" + windowMilliseconds);
                }

                try
                {
                    // Obtain a fresh element every retry. Chrome can replace the renderer
                    // host while the first navigation is still under way.
                    AutomationElement window = AutomationElement.FromHandle(process.MainWindowHandle);
                    if (window == null)
                    {
                        lastReason = "Chrome main window exists but its UIA root node is unavailable";
                    }
                    else
                    {
                        string rootClassName = window.Current.ClassName;
                        diagnostics.WriteOnce("uia-root-" + processId, "uia-root pid=" + processId + "; class=" + rootClassName);
                        AutomationElement document = window.FindFirst(
                            TreeScope.Descendants,
                            new OrCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                                new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_RenderWidgetHostHWND")));
                        if (document != null)
                        {
                            long documentMilliseconds = stopwatch.ElapsedMilliseconds;
                            diagnostics.Write("uia-document pid=" + processId + "; elapsed-ms=" + documentMilliseconds);
                            return BrowserReadyResult.Ready(processId, windowMilliseconds, documentMilliseconds, processRaceCount);
                        }
                        lastReason = "Chrome main window UIA root is available but its Document node is not ready";
                    }
                }
                catch (COMException ex)
                {
                    lastUiaExceptionHResult = ex.HResult;
                    lastReason = "UIA query failed: COMException; hresult=0x" + ex.HResult.ToString("X8") + "; " + ex.Message;
                    diagnostics.WriteOnce("uia-com-" + ex.HResult, "uia-com-exception pid=" + processId + "; hresult=0x" + ex.HResult.ToString("X8") + "; message=" + ex.Message);
                }
                catch (Exception ex)
                {
                    lastReason = "UIA query failed: " + ex.GetType().Name + "; " + ex.Message;
                    diagnostics.WriteOnce("uia-ex-" + ex.GetType().FullName, "uia-exception pid=" + processId + "; type=" + ex.GetType().FullName + "; message=" + ex.Message);
                }
                Thread.Sleep(100);
            }

            return BrowserReadyResult.NotReady(processId, windowMilliseconds,
                lastReason + "; timeout-ms=" + BrowserReadyTimeoutMilliseconds,
                processRaceCount, lastUiaExceptionHResult);
        }

        private static ChromeProcessDiscovery FindMaxwellChromeProcess(string chromePath, string profile, BrowserDiagnostics diagnostics)
        {
            string normalizedChrome = Path.GetFullPath(chromePath);
            List<ChromeProcessInfo> browserCandidates = new List<ChromeProcessInfo>();
            int childCandidates = 0;
            int processRaceCount = 0;

            foreach (ManagementObject process in new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='chrome.exe'").Get())
            {
                string executablePath = process["ExecutablePath"] as string;
                string commandLine = process["CommandLine"] as string;
                if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(commandLine)) continue;
                try
                {
                    if (!string.Equals(Path.GetFullPath(executablePath), normalizedChrome, StringComparison.OrdinalIgnoreCase)) continue;
                }
                catch
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(profile) && commandLine.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int pid = Convert.ToInt32((uint)process["ProcessId"]);
                if (IsChromeChildProcess(commandLine))
                {
                    childCandidates++;
                    continue;
                }

                try
                {
                    using (Process liveProcess = Process.GetProcessById(pid))
                    {
                        liveProcess.Refresh();
                        if (liveProcess.HasExited)
                        {
                            processRaceCount++;
                            diagnostics.Write("process-race pid=" + pid + "; state=already-exited");
                            continue;
                        }
                        IntPtr mainWindowHandle = liveProcess.MainWindowHandle;
                        bool isVisible = mainWindowHandle != IntPtr.Zero && IsWindowVisible(mainWindowHandle);
                        browserCandidates.Add(new ChromeProcessInfo(pid, mainWindowHandle, isVisible, commandLine));
                    }
                }
                catch (ArgumentException ex)
                {
                    processRaceCount++;
                    diagnostics.Write("process-race pid=" + pid + "; type=" + ex.GetType().Name + "; message=" + ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    processRaceCount++;
                    diagnostics.Write("process-race pid=" + pid + "; type=" + ex.GetType().Name + "; message=" + ex.Message);
                }
                catch (Win32Exception ex)
                {
                    processRaceCount++;
                    diagnostics.Write("process-race pid=" + pid + "; type=" + ex.GetType().Name + "; message=" + ex.Message);
                }
            }

            ChromeProcessInfo browserProcess = browserCandidates
                .OrderByDescending(candidate => candidate.IsVisible)
                .ThenByDescending(candidate => candidate.MainWindowHandle != IntPtr.Zero)
                .ThenBy(candidate => candidate.ProcessId)
                .FirstOrDefault();
            return new ChromeProcessDiscovery(browserProcess, browserCandidates.Count, childCandidates, processRaceCount);
        }

        private static bool IsChromeChildProcess(string commandLine)
        {
            return commandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                commandLine.IndexOf("--type ", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private sealed class ChromeProcessDiscovery
        {
            public ChromeProcessDiscovery(ChromeProcessInfo browserProcess, int browserCandidates, int childCandidates, int processRaceCount)
            {
                BrowserProcess = browserProcess;
                BrowserCandidates = browserCandidates;
                ChildCandidates = childCandidates;
                ProcessRaceCount = processRaceCount;
            }
            public ChromeProcessInfo BrowserProcess { get; private set; }
            public int BrowserCandidates { get; private set; }
            public int ChildCandidates { get; private set; }
            public int ProcessRaceCount { get; private set; }
        }

        private sealed class ChromeProcessInfo
        {
            public ChromeProcessInfo(int processId, IntPtr mainWindowHandle, bool isVisible, string commandLine)
            {
                ProcessId = processId;
                MainWindowHandle = mainWindowHandle;
                IsVisible = isVisible;
                CommandLine = commandLine;
            }
            public int ProcessId { get; private set; }
            public IntPtr MainWindowHandle { get; private set; }
            public bool IsVisible { get; private set; }
            public string CommandLine { get; private set; }
        }

        private sealed class BrowserReadyResult
        {
            public bool IsReady { get; private set; }
            public int ProcessId { get; private set; }
            public long WindowMilliseconds { get; private set; }
            public long DocumentMilliseconds { get; private set; }
            public string Message { get; private set; }
            public int ProcessRaceCount { get; private set; }
            public int? LastUiaExceptionHResult { get; private set; }

            public static BrowserReadyResult Ready(int processId, long windowMilliseconds, long documentMilliseconds, int processRaceCount)
            {
                return new BrowserReadyResult { IsReady = true, ProcessId = processId, WindowMilliseconds = windowMilliseconds, DocumentMilliseconds = documentMilliseconds, ProcessRaceCount = processRaceCount };
            }
            public static BrowserReadyResult NotReady(int processId, long windowMilliseconds, string message, int processRaceCount, int? lastUiaExceptionHResult)
            {
                return new BrowserReadyResult { IsReady = false, ProcessId = processId, WindowMilliseconds = windowMilliseconds, Message = message, ProcessRaceCount = processRaceCount, LastUiaExceptionHResult = lastUiaExceptionHResult };
            }
        }

        private sealed class BrowserDiagnostics : IDisposable
        {
            private readonly StreamWriter writer;
            private readonly HashSet<string> onceKeys = new HashSet<string>(StringComparer.Ordinal);
            public BrowserDiagnostics(string directory)
            {
                if (string.IsNullOrWhiteSpace(directory)) directory = Path.GetTempPath();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "chrome-uia-launch-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".log");
                writer = new StreamWriter(path, false) { AutoFlush = true };
                Write("diagnostic-file=" + path);
            }
            public void Write(string message)
            {
                writer.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message);
            }
            public void WriteOnce(string key, string message)
            {
                if (onceKeys.Add(key)) Write(message);
            }
            public void Dispose() { writer.Dispose(); }
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
