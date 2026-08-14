using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Activities;
using System.Activities.XamlIntegration;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xaml;
using Microsoft.Win32;
using OpenRPA.Interfaces;

namespace Maxwell.RuntimeHost
{
    internal static class Program
    {
        private static readonly ManualResetEventSlim Finished = new ManualResetEventSlim(false);
        private static readonly HashSet<string> FrameworkAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib", "Microsoft.CSharp", "Microsoft.VisualBasic", "System", "System.Activities",
            "System.Activities.Core.Presentation", "System.Activities.DurableInstancing",
            "System.Activities.Presentation", "System.ComponentModel.Composition", "System.Configuration",
            "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Drawing", "System.Management",
            "System.Net.Http", "System.Runtime", "System.Runtime.Serialization", "System.Runtime.WindowsRuntime", "System.ServiceModel",
            "System.Xaml", "System.Xml", "System.Xml.Linq", "UIAutomationClient", "UIAutomationTypes",
            "WindowsBase", "PresentationCore", "PresentationFramework"
        };
        private static readonly Regex DefaultChromeSelectorPath = new Regex(
            @"(?<prefix>&quot;filename&quot;\s*:\s*&quot;)(?:(?:%ProgramFiles(?:\(x86\))?%)|(?:C:\\\\Program Files(?: \(x86\))?))\\\\Google\\\\Chrome\\\\Application\\\\chrome\.exe(?<suffix>&quot;)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex SelectorAttribute = new Regex(
            @"(?<prefix>\bSelector="")(?<value>[^""]*)(?<suffix>"")",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex StartProcessTag = new Regex(
            @"<(?:\w+:)?StartProcess\b[^>]*?/?>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex FilenameAttribute = new Regex(
            @"\bFilename=""(?<value>[^""]*)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex ArgumentsAttribute = new Regex(
            @"\bArguments=""(?<value>[^""]*)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex InvokeOpenRpaTag = new Regex(
            @"<(?:\w+:)?InvokeOpenRPA\b[^>]*\bworkflow=""(?<value>[^""]+)""[^>]*/?>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private const int ChromeConnectionTimeoutMilliseconds = 30000;
        private const string OpenRpaChromeExtensionId = "hpnihnhlcnfejboocnckgchjdofeaphe";
        private const string MaxwellNativeHostName = "com.maxwell.openrpa.msg";
        private const string OfficialNativeHostName = "com.openrpa.msg";

        private static string _runtimeDirectory;
        private static string _resultFile;
        private static WorkflowApplication _application;
        private static RuntimeResponse _result;

        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            try
            {
                EnsureWpfApplication();
                Options options = Options.Parse(args);
                _resultFile = options.ResultFile;
                _runtimeDirectory = options.RuntimeDirectory;
                Environment.SetEnvironmentVariable("MAXWELL_NOTIFICATION_HOST", Path.Combine(_runtimeDirectory, "Maxwell.NotificationHost.exe"));
                WorkflowDocument workflow = WorkflowDocument.Load(options.WorkflowFile);
                DependencyReport dependencies = DependencyReport.Create(workflow.Xaml, _runtimeDirectory);

                if (options.Command == "inspect")
                {
                    Write(new RuntimeResponse
                    {
                        success = dependencies.MissingAssemblies.Count == 0,
                        action = dependencies.MissingAssemblies.Count == 0 ? "compatible" : "incompatible",
                        errorCode = dependencies.MissingAssemblies.Count == 0 ? null : "missing_assemblies",
                        error = dependencies.MissingAssemblies.Count == 0
                            ? null
                            : "缺少 workflow 所需程序集：" + string.Join("、", dependencies.MissingAssemblies),
                        workflowName = workflow.Name,
                        requiredAssemblies = dependencies.RequiredAssemblies,
                        missingAssemblies = dependencies.MissingAssemblies
                    });
                    return dependencies.MissingAssemblies.Count == 0 ? 0 : 2;
                }

                if (dependencies.MissingAssemblies.Count > 0)
                {
                    throw new RuntimeFailureException(
                        "missing_assemblies",
                        "缺少 workflow 所需程序集：" + string.Join("、", dependencies.MissingAssemblies));
                }

                // Browser automation can live in a workflow reached through
                // InvokeOpenRPA. Detect the complete call tree before deciding
                // whether Native Messaging must be registered.
                bool requiresChromeAutomation = RequiresChromeAutomation(workflow, options.WorkflowRoot);
                string chromePreflightUrl = FindChromeLaunchUrl(workflow, options.WorkflowRoot);
                if (requiresChromeAutomation)
                {
                    EnsureBrowserNativeMessagingRegistration(_runtimeDirectory);
                }

                Dictionary<string, object> inputs = options.LoadArguments();
                return Run(workflow, dependencies, inputs, options.WorkflowRoot, requiresChromeAutomation, chromePreflightUrl);
            }
            catch (RuntimeFailureException ex)
            {
                Write(new RuntimeResponse { success = false, action = "failed", errorCode = ex.Code, error = ex.Message });
                return 2;
            }
            catch (Exception ex)
            {
                Exception actual = Unwrap(ex);
                Write(new RuntimeResponse
                {
                    success = false,
                    action = "failed",
                    errorCode = "runtime_error",
                    error = actual.Message,
                    details = actual.ToString()
                });
                return 1;
            }
        }

        private static void EnsureWpfApplication()
        {
            if (System.Windows.Application.Current != null) return;
            new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };
        }

        private static void EnsureBrowserNativeMessagingRegistration(string runtimeDirectory)
        {
            string localHostDirectory = Environment.GetEnvironmentVariable("MAXWELL_NATIVE_HOST_DIRECTORY");
            string hostDirectory = !string.IsNullOrWhiteSpace(localHostDirectory) &&
                                   File.Exists(Path.Combine(localHostDirectory, "OpenRPA.NativeMessagingHost.exe"))
                ? localHostDirectory
                : runtimeDirectory;
            string hostExecutable = Path.Combine(hostDirectory, "OpenRPA.NativeMessagingHost.exe");
            string manifestPath = Path.Combine(hostDirectory, "chromemanifest.json");
            string templatePath = Path.Combine(hostDirectory, "chromemanifest.template.json");
            if (!File.Exists(hostExecutable))
            {
                throw new RuntimeFailureException(
                    "browser_runtime_incomplete",
                    "浏览器自动化运行库不完整，缺少 OpenRPA.NativeMessagingHost.exe：" + hostExecutable);
            }
            if (!File.Exists(templatePath) && !File.Exists(manifestPath))
            {
                throw new RuntimeFailureException(
                    "browser_runtime_incomplete",
                    "浏览器自动化运行库不完整，缺少 Native Messaging manifest：" + manifestPath);
            }

            string sourceManifest = File.Exists(templatePath)
                ? File.ReadAllText(templatePath)
                : File.ReadAllText(manifestPath);
            if (!Regex.IsMatch(sourceManifest, @"""path""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(sourceManifest, @"""name""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant))
            {
                throw new RuntimeFailureException(
                    "browser_manifest_invalid",
                    "Native Messaging manifest 缺少 name 或 path：" + manifestPath);
            }
            string escapedHostPath = hostExecutable.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string updatedManifest = Regex.Replace(
                sourceManifest,
                @"(""path""\s*:\s*"")[^""]*("")",
                match => match.Groups[1].Value + escapedHostPath + match.Groups[2].Value,
                RegexOptions.CultureInvariant);
            updatedManifest = Regex.Replace(
                updatedManifest,
                @"(""name""\s*:\s*"")[^""]*("")",
                match => match.Groups[1].Value + MaxwellNativeHostName + match.Groups[2].Value,
                RegexOptions.CultureInvariant);
            // The Chrome Web Store extension bundled in the isolated Maxwell
            // profile still requests com.openrpa.msg. Keep a second manifest
            // for that extension; edit mode restores the installed OpenRPA
            // manifest before users return to the designer.
            string officialManifest = Regex.Replace(
                updatedManifest,
                @"(""name""\s*:\s*"")[^""]*("")",
                match => match.Groups[1].Value + OfficialNativeHostName + match.Groups[2].Value,
                RegexOptions.CultureInvariant);
            string officialManifestPath = Path.Combine(hostDirectory, "chromemanifest.openrpa.json");
            if (updatedManifest.IndexOf(escapedHostPath, StringComparison.Ordinal) < 0)
            {
                throw new RuntimeFailureException(
                    "browser_manifest_invalid",
                    "Unable to update the browser Native Messaging manifest: " + manifestPath);
            }

            File.WriteAllText(manifestPath, updatedManifest, new UTF8Encoding(false));
            File.WriteAllText(officialManifestPath, officialManifest, new UTF8Encoding(false));
            RegisterBrowserNativeMessagingHost(@"Software\Google\Chrome\NativeMessagingHosts\com.maxwell.openrpa.msg", manifestPath);
            RegisterBrowserNativeMessagingHost(@"Software\Microsoft\Edge\NativeMessagingHosts\com.maxwell.openrpa.msg", manifestPath);
            RegisterBrowserNativeMessagingHost(@"Software\Google\Chrome\NativeMessagingHosts\com.openrpa.msg", officialManifestPath);
            RegisterBrowserNativeMessagingHost(@"Software\Microsoft\Edge\NativeMessagingHosts\com.openrpa.msg", officialManifestPath);
        }

        private static void RegisterBrowserNativeMessagingHost(string registryPath, string manifestPath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath))
            {
                key?.SetValue(string.Empty, manifestPath, RegistryValueKind.String);
            }
        }

        private static int Run(
            WorkflowDocument workflow,
            DependencyReport dependencies,
            Dictionary<string, object> inputs,
            string workflowRoot,
            bool requiresChromeAutomation,
            string chromePreflightUrl)
        {
            string failureActivity = null;
            Mutex browserRunMutex = null;
            bool ownsBrowserRunMutex = false;
            try
            {
                if (requiresChromeAutomation)
                {
                    string mutexName = @"Local\Maxwell.BrowserAutomation." +
                        System.Diagnostics.Process.GetCurrentProcess().SessionId;
                    browserRunMutex = new Mutex(false, mutexName);
                    try
                    {
                        ownsBrowserRunMutex = browserRunMutex.WaitOne(TimeSpan.Zero);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsBrowserRunMutex = true;
                    }
                    if (!ownsBrowserRunMutex)
                    {
                        throw new RuntimeFailureException(
                            "browser_automation_busy",
                            "当前 Windows 会话中已有浏览器自动化流程正在执行。请等待该流程结束后重试。");
                    }
                }

            foreach (string assemblyName in dependencies.RequiredAssemblies)
            {
                // Framework assemblies are resolved by .NET Framework itself. Loading
                // facade assemblies such as System.Runtime.WindowsRuntime by partial
                // name is unreliable even though XAML compilation can resolve them.
                if (FrameworkAssemblies.Contains(assemblyName)) continue;
                LoadAssembly(assemblyName, _runtimeDirectory);
            }

            if (requiresChromeAutomation &&
                !dependencies.RequiredAssemblies.Any(name => string.Equals(name, "OpenRPA.NM", StringComparison.OrdinalIgnoreCase)))
            {
                // A root workflow may reach browser automation only through
                // InvokeOpenRPA. Load NM before plugin discovery so its pipe client
                // is initialized even though the root XAML has no NM namespace.
                LoadAssembly("OpenRPA.NM", _runtimeDirectory);
                dependencies.RequiredAssemblies.Add("OpenRPA.NM");
                dependencies.RequiredAssemblies.Sort(StringComparer.OrdinalIgnoreCase);
            }

            OpenRpaRuntimeBootstrap openRpaBootstrap = OpenRpaRuntimeBootstrap.TryInitialize(
                dependencies.RequiredAssemblies,
                _runtimeDirectory,
                workflowRoot,
                workflow.SourceFile);

            if (requiresChromeAutomation)
            {
                EnsureChromeAutomationConnected(_runtimeDirectory, chromePreflightUrl);
                Write(new RuntimeResponse
                {
                    success = true,
                    action = "browser_automation_ready",
                    workflowName = workflow.Name,
                    details = "Chrome 浏览器扩展已与 Maxwell RuntimeHost 建立实际连接。"
                });
            }

            if (!string.IsNullOrWhiteSpace(workflow.Culture))
            {
                CultureInfo culture = new CultureInfo(workflow.Culture);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }

            Assembly localAssembly = FindLoadedAssembly("OpenRPA") ?? Assembly.GetExecutingAssembly();
            XamlXmlReaderSettings readerSettings = new XamlXmlReaderSettings { LocalAssembly = localAssembly };
            int rootLegacySelectorConversions;
            int rootChromeSelectorBindings;
            int rootChromeLaunchBindings;
            string executableXaml = ApplyBundledChromeCompatibility(
                workflow.Xaml,
                out rootLegacySelectorConversions,
                out rootChromeSelectorBindings,
                out rootChromeLaunchBindings);
            if (rootChromeSelectorBindings > 0 || rootChromeLaunchBindings > 0)
            {
                Write(new RuntimeResponse
                {
                    success = true,
                    action = "browser_selector_compatibility",
                    workflowName = workflow.Name,
                    details = "Bundled Chrome binding: selectors " + rootChromeSelectorBindings + ", StartProcess " + rootChromeLaunchBindings + "."
                });
            }
            Activity activity;
            using (StringReader stringReader = new StringReader(executableXaml))
            using (XamlXmlReader xamlReader = new XamlXmlReader(stringReader, readerSettings))
            {
                activity = ActivityXamlServices.Load(xamlReader, new ActivityXamlServicesSettings
                {
                    CompileExpressions = true
                });
            }

            _result = new RuntimeResponse
            {
                success = false,
                action = "running",
                workflowName = workflow.Name,
                requiredAssemblies = dependencies.RequiredAssemblies,
                missingAssemblies = dependencies.MissingAssemblies
            };

            Write(new RuntimeResponse { success = true, action = "started", workflowName = workflow.Name });
            _application = inputs.Count == 0
                ? new WorkflowApplication(activity)
                : new WorkflowApplication(activity, inputs);
            openRpaBootstrap?.AddWorkflowExtensions(_application);
            openRpaBootstrap?.RegisterRootWorkflowExecution(_application.Id);

            _application.Completed = completed =>
            {
                Dictionary<string, object> outputs = completed.Outputs.ToDictionary(item => item.Key, item => item.Value);
                Exception terminationException = completed.TerminationException == null ? null : Unwrap(completed.TerminationException);
                bool completedSuccessfully = completed.CompletionState == ActivityInstanceState.Closed && terminationException == null;
                bool canceled = completed.CompletionState == ActivityInstanceState.Canceled && terminationException == null;
                _result = new RuntimeResponse
                {
                    success = completedSuccessfully,
                    action = completedSuccessfully ? "completed" : "failed",
                    errorCode = completedSuccessfully
                        ? null
                        : terminationException != null
                            ? "workflow_faulted"
                            : canceled ? "workflow_canceled" : "workflow_incomplete",
                    error = completedSuccessfully
                        ? null
                        : terminationException != null
                            ? terminationException.Message
                            : canceled
                                ? "workflow 已取消，未执行完成。"
                                : "workflow 未以 Closed 状态结束：" + completed.CompletionState,
                    details = completedSuccessfully
                        ? null
                        : terminationException != null
                            ? BuildFailureDetails(workflow, terminationException, failureActivity)
                            : "工作流: " + workflow.Name + Environment.NewLine +
                              "源文件: " + workflow.SourceFile + Environment.NewLine +
                              "完成状态: " + completed.CompletionState,
                    workflowName = workflow.Name,
                    outputKeys = outputs.Keys.OrderBy(value => value).ToList(),
                    outputs = outputs,
                    requiredAssemblies = dependencies.RequiredAssemblies,
                    missingAssemblies = dependencies.MissingAssemblies
                };
                Finished.Set();
            };
            _application.Aborted = aborted =>
            {
                Exception actual = Unwrap(aborted.Reason);
                _result = new RuntimeResponse
                {
                    success = false,
                    action = "aborted",
                    errorCode = "workflow_aborted",
                    error = actual.Message,
                    details = BuildFailureDetails(workflow, actual, failureActivity),
                    workflowName = workflow.Name
                };
                Finished.Set();
            };
            _application.OnUnhandledException = unhandled =>
            {
                Exception actual = Unwrap(unhandled.UnhandledException);
                failureActivity = unhandled.ExceptionSource == null
                    ? null
                    : unhandled.ExceptionSource.DisplayName + " (" + unhandled.ExceptionSource.GetType().FullName + ")";
                _result = new RuntimeResponse
                {
                    success = false,
                    action = "failed",
                    errorCode = "workflow_unhandled_exception",
                    error = actual.Message,
                    details = BuildFailureDetails(workflow, actual, failureActivity),
                    workflowName = workflow.Name
                };
                return UnhandledExceptionAction.Terminate;
            };
            _application.Idle = idle =>
            {
                Write(new RuntimeResponse
                {
                    success = true,
                    action = "idle",
                    workflowName = workflow.Name,
                    bookmarks = idle.Bookmarks.Select(bookmark => bookmark.BookmarkName).ToList()
                });
            };

            _application.Run();
            WaitForCompletion();
            Write(_result);
            return _result.success ? 0 : 1;
            }
            finally
            {
                if (ownsBrowserRunMutex) browserRunMutex.ReleaseMutex();
                if (browserRunMutex != null) browserRunMutex.Dispose();
            }
        }

        private static bool RequiresChromeAutomation(WorkflowDocument rootWorkflow, string workflowRoot)
        {
            Dictionary<string, WorkflowDocument> workflows = new Dictionary<string, WorkflowDocument>(StringComparer.OrdinalIgnoreCase);
            AddWorkflowLookupKeys(workflows, rootWorkflow);

            if (!string.IsNullOrWhiteSpace(workflowRoot) && Directory.Exists(workflowRoot))
            {
                foreach (string path in Directory.EnumerateFiles(workflowRoot, "*.json", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(path), rootWorkflow.SourceFile, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        AddWorkflowLookupKeys(workflows, WorkflowDocument.Load(path));
                    }
                    catch
                    {
                        // Registry loading reports malformed project files later. Browser
                        // preflight discovery must not change that existing error path.
                    }
                }
            }

            return RequiresChromeAutomation(
                rootWorkflow,
                workflows,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private static string FindChromeLaunchUrl(WorkflowDocument rootWorkflow, string workflowRoot)
        {
            Dictionary<string, WorkflowDocument> workflows = new Dictionary<string, WorkflowDocument>(StringComparer.OrdinalIgnoreCase);
            AddWorkflowLookupKeys(workflows, rootWorkflow);
            if (!string.IsNullOrWhiteSpace(workflowRoot) && Directory.Exists(workflowRoot))
            {
                foreach (string path in Directory.EnumerateFiles(workflowRoot, "*.json", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFullPath(path), rootWorkflow.SourceFile, StringComparison.OrdinalIgnoreCase)) continue;
                    try { AddWorkflowLookupKeys(workflows, WorkflowDocument.Load(path)); }
                    catch { }
                }
            }
            return FindChromeLaunchUrl(rootWorkflow, workflows, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private static string FindChromeLaunchUrl(
            WorkflowDocument workflow,
            IReadOnlyDictionary<string, WorkflowDocument> workflows,
            HashSet<string> visited)
        {
            if (!visited.Add(workflow.SourceFile)) return null;
            foreach (Match tag in StartProcessTag.Matches(workflow.Xaml))
            {
                Match filename = FilenameAttribute.Match(tag.Value);
                Match arguments = ArgumentsAttribute.Match(tag.Value);
                if (!filename.Success || !arguments.Success ||
                    !IsChromeLaunchTarget(System.Net.WebUtility.HtmlDecode(filename.Groups["value"].Value))) continue;
                string value = System.Net.WebUtility.HtmlDecode(arguments.Groups["value"].Value).Trim().Trim('"');
                Uri uri;
                if (Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return value;
            }
            foreach (Match match in InvokeOpenRpaTag.Matches(workflow.Xaml))
            {
                string reference = NormalizeWorkflowReference(System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value));
                WorkflowDocument child;
                string result;
                if (workflows.TryGetValue(reference, out child) &&
                    !string.IsNullOrWhiteSpace(result = FindChromeLaunchUrl(child, workflows, visited))) return result;
            }
            return null;
        }

        private static bool RequiresChromeAutomation(
            WorkflowDocument workflow,
            IReadOnlyDictionary<string, WorkflowDocument> workflows,
            HashSet<string> visited)
        {
            if (!visited.Add(workflow.SourceFile)) return false;
            if (Regex.IsMatch(workflow.Xaml, @"assembly\s*=\s*OpenRPA\.NM(?:[;\""'\s]|$)", RegexOptions.IgnoreCase) ||
                workflow.Xaml.IndexOf("clr-namespace:OpenRPA.NM", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            foreach (Match match in InvokeOpenRpaTag.Matches(workflow.Xaml))
            {
                string reference = NormalizeWorkflowReference(System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value));
                WorkflowDocument child;
                if (workflows.TryGetValue(reference, out child) &&
                    RequiresChromeAutomation(child, workflows, visited))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddWorkflowLookupKeys(IDictionary<string, WorkflowDocument> workflows, WorkflowDocument workflow)
        {
            if (!string.IsNullOrWhiteSpace(workflow.ProjectAndName))
                workflows[NormalizeWorkflowReference(workflow.ProjectAndName)] = workflow;
            if (!string.IsNullOrWhiteSpace(workflow.Name))
            {
                string name = NormalizeWorkflowReference(workflow.Name);
                if (!workflows.ContainsKey(name)) workflows[name] = workflow;
            }
        }

        private static string NormalizeWorkflowReference(string value)
        {
            return (value ?? string.Empty).Replace('/', '\\').Trim();
        }

        private static void EnsureChromeAutomationConnected(string runtimeDirectory, string workflowUrl)
        {
            Assembly nmAssembly = LoadAssembly("OpenRPA.NM", runtimeDirectory, false);
            Type hookType = nmAssembly == null ? null : nmAssembly.GetType("OpenRPA.NM.NMHook", false, false);
            PropertyInfo connectedProperty = hookType == null
                ? null
                : hookType.GetProperty("chromeconnected", BindingFlags.Public | BindingFlags.Static);
            if (connectedProperty == null)
            {
                throw new RuntimeFailureException(
                    "browser_connection_probe_unavailable",
                    "无法读取 OpenRPA 的 Chrome 实际连接状态；请确认 Maxwell 运行库完整。"
                );
            }

            MethodInfo probeMethod = hookType.GetMethod(
                "sendMessageChromeResult",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(NativeMessagingMessage), typeof(TimeSpan) },
                null);
            if (probeMethod == null)
            {
                throw new RuntimeFailureException(
                    "browser_connection_probe_unavailable",
                    "OpenRPA Chrome connection probe is unavailable. Rebuild the Maxwell package.");
            }

            Func<bool> isConnected = () => (bool)(connectedProperty.GetValue(null, null) ?? false);
            Func<bool> browserReplies = () => TryProbeChromeAutomation(isConnected, probeMethod);
            // checkForPipes starts its named-pipe client asynchronously. Give an
            // already-running, healthy browser bridge time to accept that client;
            // killing the host during this window caused the extension's reconnect
            // handlers to race and was the main source of intermittent cold starts.
            System.Diagnostics.Stopwatch existingConnection = System.Diagnostics.Stopwatch.StartNew();
            while (existingConnection.ElapsedMilliseconds < 3000)
            {
                if (browserReplies()) return;
                Thread.Sleep(100);
            }

            // An enabled MV3 extension can still hold a native-messaging port
            // whose host belongs to a previous RuntimeHost instance. The icon
            // and site permission remain healthy, but that stale pipe never
            // answers enumtabs. Recreate only Maxwell's local native host, then
            // wake the extension so its disconnect handler reconnects it.
            ResetStaleMaxwellNativeHostConnection();
            string preflightUrl = StartChromeExtensionBackground(workflowUrl);
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < ChromeConnectionTimeoutMilliseconds)
            {
                if (browserReplies())
                {
                    return;
                }
                Thread.Sleep(100);
            }

            throw new RuntimeFailureException(
                "browser_not_connected",
                "已检测到 OpenRPA 扩展，但扩展与 Native Messaging Host 的自动化通道未在规定时间内回复。" +
                "Maxwell 已尝试重建连接，项目流程未开始执行。请完全关闭 Chrome 后重试。"
            );
        }

        private static bool TryProbeChromeAutomation(Func<bool> isConnected, MethodInfo probeMethod)
        {
            if (!isConnected()) return false;

            try
            {
                // Verify a real request/reply round trip. A connected named
                // pipe by itself can be stale while Chrome is still unusable.
                NativeMessagingMessage request = new NativeMessagingMessage("enumtabs", false, null)
                {
                    browser = "chrome"
                };
                object reply = probeMethod.Invoke(null, new object[] { request, TimeSpan.FromSeconds(1) });
                return reply != null;
            }
            catch
            {
                return false;
            }
        }

        private static void ResetStaleMaxwellNativeHostConnection()
        {
            string hostDirectory = Environment.GetEnvironmentVariable("MAXWELL_NATIVE_HOST_DIRECTORY");
            if (string.IsNullOrWhiteSpace(hostDirectory)) return;
            string expectedHost = Path.GetFullPath(Path.Combine(hostDirectory, "OpenRPA.NativeMessagingHost.exe"));

            foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcessesByName("OpenRPA.NativeMessagingHost"))
            {
                using (process)
                {
                    try
                    {
                        string actualHost = process.MainModule == null ? null : process.MainModule.FileName;
                        if (string.IsNullOrWhiteSpace(actualHost) ||
                            !string.Equals(Path.GetFullPath(actualHost), expectedHost, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // The native host owns the named-pipe server. A host left by a
                        // previous RuntimeHost can retain a dead client connection, so
                        // let the extension recreate only Maxwell's own host process.
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                    catch
                    {
                        // If the process exits during inspection the extension will
                        // recreate it naturally; connection polling remains authoritative.
                    }
                }
            }
        }

        private static string StartChromeExtensionBackground(string workflowUrl)
        {
            string chromePath = ResolveChromeExecutable();
            if (string.IsNullOrWhiteSpace(chromePath) || !File.Exists(chromePath))
            {
                throw new RuntimeFailureException(
                    "chrome_not_found",
                    "未找到可用于浏览器自动化预检的 Chrome。"
                );
            }

            List<string> arguments = new List<string>
            {
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-default-apps",
                "--new-window"
            };
            string profile = Environment.GetEnvironmentVariable("MAXWELL_BROWSER_PROFILE");
            if (!string.IsNullOrWhiteSpace(profile))
            {
                arguments.Add("--user-data-dir=" + QuoteCommandLineArgument(profile));
                arguments.Add("--profile-directory=Default");
            }
            string extension = Environment.GetEnvironmentVariable("MAXWELL_OPENRPA_EXTENSION");
            if (!string.IsNullOrWhiteSpace(extension) && Directory.Exists(extension))
            {
                arguments.Add("--disable-extensions-except=" + QuoteCommandLineArgument(extension));
                arguments.Add("--load-extension=" + QuoteCommandLineArgument(extension));
            }
            // Maxwell's private extension connects from its service-worker
            // startup handler. A neutral page is sufficient and avoids exposing
            // the OpenRPA settings page or pre-opening the workflow's business URL.
            string preflightUrl = "about:blank";
            arguments.Add(QuoteCommandLineArgument(preflightUrl));

            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = chromePath,
                    Arguments = string.Join(" ", arguments),
                    WorkingDirectory = Path.GetDirectoryName(chromePath),
                    // Do not let the long-lived browser inherit RuntimeHost's
                    // redirected stdout/stderr handles. Otherwise Maxwell's
                    // ReadToEnd waits until Chrome closes even after the
                    // workflow process has already exited.
                    UseShellExecute = true
                }))
                {
                    // Chrome usually forwards this request to its existing browser
                    // process, so the short-lived returned process is not awaited.
                }
            }
            catch (Exception ex)
            {
                throw new RuntimeFailureException(
                    "chrome_preflight_start_failed",
                    "无法启动 Chrome 浏览器自动化预检：" + Unwrap(ex).Message
                );
            }

            return preflightUrl;
        }

        private static void StartPreflightTabCleanup(Type hookType, string preflightUrl)
        {
            // Keep the preflight tab alive until the workflow opens or attaches to
            // a real tab. Closing Chrome's only tab here would tear down the exact
            // connection that was just verified.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    MethodInfo enumerateTabs = hookType.GetMethod("enumwindowandtabs", BindingFlags.Public | BindingFlags.Static);
                    MethodInfo closeTab = hookType.GetMethod(
                        "CloseTab",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(string), typeof(int) },
                        null);
                    FieldInfo tabsField = hookType.GetField("tabs", BindingFlags.Public | BindingFlags.Static);
                    if (enumerateTabs == null || closeTab == null || tabsField == null) return;

                    for (int attempt = 0; attempt < 300; attempt++)
                    {
                        enumerateTabs.Invoke(null, null);
                        Thread.Sleep(100);
                        IEnumerable tabs = tabsField.GetValue(null) as IEnumerable;
                        if (tabs == null) continue;

                        object preflightTab = null;
                        bool hasRealChromeTab = false;
                        foreach (object tab in tabs.Cast<object>().ToList())
                        {
                            Type tabType = tab.GetType();
                            string browser = ReadStringMember(tabType, tab, "browser");
                            string url = ReadStringMember(tabType, tab, "url");
                            if (!string.Equals(browser, "chrome", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.Equals(url, preflightUrl, StringComparison.OrdinalIgnoreCase)) preflightTab = tab;
                            else if (!string.IsNullOrWhiteSpace(url)) hasRealChromeTab = true;
                        }

                        if (preflightTab == null) return; // Chrome reused the tab.
                        if (!hasRealChromeTab)
                        {
                            Thread.Sleep(100);
                            continue;
                        }

                        int tabId = ReadIntMember(preflightTab.GetType(), preflightTab, "id");
                        closeTab.Invoke(null, new object[] { "chrome", tabId });
                        return;
                    }
                }
                catch
                {
                    // Cleanup is cosmetic. It must never alter workflow execution.
                }
            });
        }

        private static string ReadStringMember(Type type, object instance, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(instance, null) as string;
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(instance) as string;
        }

        private static int ReadIntMember(Type type, object instance, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            object value = property == null
                ? type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
                : property.GetValue(instance, null);
            return value == null ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static string ResolveChromeExecutable()
        {
            string bundled = Environment.GetEnvironmentVariable("MAXWELL_BUNDLED_CHROME");
            if (!string.IsNullOrWhiteSpace(bundled) && File.Exists(bundled)) return bundled;

            string[] registryPaths =
            {
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"
            };
            foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (string path in registryPaths)
                {
                    using (RegistryKey key = root.OpenSubKey(path))
                    {
                        string candidate = key == null ? null : key.GetValue(string.Empty) as string;
                        if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
                    }
                }
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            return new[]
            {
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")
            }.FirstOrDefault(File.Exists);
        }

        private static string QuoteCommandLineArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        // Existing OpenRPA workflows commonly record Chrome's system-wide
        // installation path. Maxwell's bundled browser is intentionally stored
        // under the current user's local profile, so adapt only that exact legacy
        // path and only in memory. The shared workflow JSON is never modified.
        private static string ApplyBundledChromeSelectorCompatibility(string xaml, out int conversionCount)
        {
            conversionCount = 0;
            string bundledChrome = Environment.GetEnvironmentVariable("MAXWELL_BUNDLED_CHROME");
            if (string.IsNullOrWhiteSpace(xaml) || string.IsNullOrWhiteSpace(bundledChrome) || !File.Exists(bundledChrome))
            {
                return xaml;
            }

            string selectorPath = bundledChrome.Replace("\\", "\\\\");
            int replacements = 0;
            string compatibleXaml = DefaultChromeSelectorPath.Replace(xaml, match =>
            {
                replacements++;
                return match.Groups["prefix"].Value + selectorPath + match.Groups["suffix"].Value;
            });
            conversionCount = replacements;
            return compatibleXaml;
        }

        private static string BindChromeSelectorsToBundledBrowser(string xaml, out int conversionCount)
        {
            conversionCount = 0;
            string bundledChrome = Environment.GetEnvironmentVariable("MAXWELL_BUNDLED_CHROME");
            if (string.IsNullOrWhiteSpace(xaml) || string.IsNullOrWhiteSpace(bundledChrome) || !File.Exists(bundledChrome)) return xaml;

            int conversions = 0;
            string compatibleXaml = SelectorAttribute.Replace(xaml, match =>
            {
                try
                {
                    string selectorText = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value);
                    int marker = selectorText.IndexOf('%');
                    if (marker < 0) return match.Value;

                    JArray selectorItems = JArray.Parse(selectorText.Substring(marker + 1));
                    int selectorConversions = 0;
                    foreach (JToken token in selectorItems)
                    {
                        JObject item = token as JObject;
                        if (item == null || !string.Equals((string)item["processname"], "chrome", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.Equals((string)item["filename"], bundledChrome, StringComparison.OrdinalIgnoreCase))
                        {
                            item["filename"] = bundledChrome;
                            selectorConversions++;
                        }
                    }
                    if (selectorConversions == 0) return match.Value;

                    conversions += selectorConversions;
                    string rewrittenSelector = selectorText.Substring(0, marker + 1) + selectorItems.ToString(Formatting.None);
                    return match.Groups["prefix"].Value + System.Net.WebUtility.HtmlEncode(rewrittenSelector) + match.Groups["suffix"].Value;
                }
                catch (JsonException)
                {
                    return match.Value;
                }
            });
            conversionCount = conversions;
            return compatibleXaml;
        }

        private static string BindChromeStartProcessesToBundledBrowser(string xaml, out int conversionCount)
        {
            conversionCount = 0;
            string bundledChrome = Environment.GetEnvironmentVariable("MAXWELL_BUNDLED_CHROME");
            string browserProfile = Environment.GetEnvironmentVariable("MAXWELL_BROWSER_PROFILE");
            if (string.IsNullOrWhiteSpace(xaml) || string.IsNullOrWhiteSpace(bundledChrome) ||
                string.IsNullOrWhiteSpace(browserProfile) || !File.Exists(bundledChrome) || !Directory.Exists(browserProfile)) return xaml;

            int conversions = 0;
            string compatibleXaml = StartProcessTag.Replace(xaml, tag =>
            {
                Match filename = FilenameAttribute.Match(tag.Value);
                if (!filename.Success || !IsChromeLaunchTarget(System.Net.WebUtility.HtmlDecode(filename.Groups["value"].Value))) return tag.Value;

                string rewrittenTag = FilenameAttribute.Replace(tag.Value,
                    "Filename=\"" + System.Net.WebUtility.HtmlEncode(bundledChrome) + "\"", 1);
                Match arguments = ArgumentsAttribute.Match(rewrittenTag);
                string profileArguments = "--user-data-dir=\"" + browserProfile + "\" --profile-directory=Default";
                if (arguments.Success)
                {
                    string existingArguments = System.Net.WebUtility.HtmlDecode(arguments.Groups["value"].Value);
                    if (existingArguments.IndexOf("--user-data-dir", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        string mergedArguments = (existingArguments + " " + profileArguments).Trim();
                        rewrittenTag = ArgumentsAttribute.Replace(rewrittenTag,
                            "Arguments=\"" + System.Net.WebUtility.HtmlEncode(mergedArguments) + "\"", 1);
                    }
                }
                else
                {
                    rewrittenTag = rewrittenTag.TrimEnd('>', '/') + " Arguments=\"" +
                        System.Net.WebUtility.HtmlEncode(profileArguments) + "\" />";
                }
                conversions++;
                return rewrittenTag;
            });
            conversionCount = conversions;
            return compatibleXaml;
        }

        private static bool IsChromeLaunchTarget(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return false;
            string normalized = filename.Trim();
            return string.Equals(normalized, "chrome", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "chrome.exe", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith("\\Google\\Chrome\\Application\\chrome.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string ApplyBundledChromeCompatibility(string xaml, out int legacySelectorConversions, out int selectorBindings, out int launchBindings)
        {
            // Workflows must run exactly as they were authored.  Rewriting their XAML
            // in a headless host can subtly change NativeActivity scheduling (in
            // particular InvokeOpenRPA/StartProcess), which is worse than a selector
            // compatibility miss: it can make the host report a false completion.
            // Browser compatibility is therefore handled outside the workflow XAML.
            legacySelectorConversions = 0;
            selectorBindings = 0;
            launchBindings = 0;
            return xaml;
        }

        private static void WaitForCompletion()
        {
            if (System.Windows.Application.Current == null)
            {
                Finished.Wait();
                return;
            }

            // OpenRPA desktop activities dispatch work back to the WPF thread.
            // Pump that dispatcher while the workflow runs instead of blocking it.
            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                System.Windows.Threading.DispatcherPriority.Background,
                delegate
                {
                    if (!Finished.IsSet) return;
                    frame.Continue = false;
                },
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            AssemblyName name = new AssemblyName(args.Name);
            return LoadAssemblyFromCandidateDirectories(name.Name, _runtimeDirectory);
        }

        private static Assembly LoadAssembly(string assemblyName, string runtimeDirectory, bool throwWhenMissing = true)
        {
            Assembly loaded = FindLoadedAssembly(assemblyName);
            if (loaded != null) return loaded;

            Assembly candidateAssembly = LoadAssemblyFromCandidateDirectories(assemblyName, runtimeDirectory);
            if (candidateAssembly != null) return candidateAssembly;

            try
            {
                return Assembly.Load(new AssemblyName(assemblyName));
            }
            catch
            {
            }

            if (throwWhenMissing) throw new FileNotFoundException("无法加载程序集 " + assemblyName + "。", assemblyName);
            return null;
        }

        private static Assembly LoadAssemblyFromCandidateDirectories(string assemblyName, string runtimeDirectory)
        {
            Assembly loaded = FindLoadedAssembly(assemblyName);
            if (loaded != null) return loaded;

            foreach (string directory in CandidateDirectories(runtimeDirectory))
            {
                foreach (string extension in new[] { ".dll", ".exe" })
                {
                    string candidate = Path.Combine(directory, assemblyName + extension);
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
            }

            return null;
        }

        private static Assembly FindLoadedAssembly(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(item => string.Equals(item.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> CandidateDirectories(string runtimeDirectory)
        {
            HashSet<string> yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in new[]
            {
                AppContext.BaseDirectory,
                runtimeDirectory,
                string.IsNullOrWhiteSpace(runtimeDirectory) ? null : Path.Combine(runtimeDirectory, "plugins"),
                Environment.CurrentDirectory
            })
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) && yielded.Add(directory))
                {
                    yield return directory;
                }
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException && exception.InnerException != null) exception = exception.InnerException;
            return exception;
        }

        private static string BuildFailureDetails(WorkflowDocument workflow, Exception exception, string failureActivity)
        {
            List<string> details = new List<string>
            {
                "工作流: " + workflow.Name,
                "源文件: " + workflow.SourceFile,
                "异常类型: " + exception.GetType().FullName,
                "原始错误: " + exception.Message
            };

            if (!string.IsNullOrWhiteSpace(failureActivity)) details.Add("失败活动: " + failureActivity);

            if (exception is FileNotFoundException || exception is DirectoryNotFoundException || exception.Message.IndexOf("找不到指定的文件", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                List<string> startProcessFiles = Regex.Matches(workflow.Xaml, @"<(?:\w+:)?StartProcess\b[^>]*\bFilename=""([^""]+)""", RegexOptions.IgnoreCase)
                    .Cast<Match>()
                    .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (startProcessFiles.Count > 0)
                    details.Add("StartProcess 候选文件: " + string.Join("；", startProcessFiles));
                details.Add("处理建议: 检查目标电脑上上述文件、快捷方式、工作目录或环境变量是否存在；不要使用当前电脑专有的绝对路径。");
            }

            details.Add("技术堆栈: " + exception);
            return string.Join(Environment.NewLine, details);
        }

        private static void Write(object value)
        {
            string json = JsonConvert.SerializeObject(value, Formatting.None);
            if (!string.IsNullOrWhiteSpace(_resultFile))
            {
                string parent = Path.GetDirectoryName(_resultFile);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(_resultFile, json, new System.Text.UTF8Encoding(false));
                return;
            }
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }

        private sealed class Options
        {
            public string Command { get; private set; }
            public string WorkflowFile { get; private set; }
            public string RuntimeDirectory { get; private set; }
            public string ArgumentsFile { get; private set; }
            public string WorkflowRoot { get; private set; }
            public string ResultFile { get; private set; }

            public static Options Parse(string[] args)
            {
                if (args.Length < 2) throw new RuntimeFailureException("invalid_arguments", "用法：Maxwell.RuntimeHost.exe <inspect|run> <workflow.json> [--runtime-dir <目录>] [--arguments <json文件>]");
                string command = args[0].ToLowerInvariant();
                if (command != "inspect" && command != "run") throw new RuntimeFailureException("invalid_command", "未知命令：" + args[0]);

                Options result = new Options
                {
                    Command = command,
                    WorkflowFile = Path.GetFullPath(args[1]),
                    RuntimeDirectory = AppContext.BaseDirectory,
                    WorkflowRoot = Path.GetDirectoryName(Path.GetFullPath(args[1]))
                };
                for (int index = 2; index < args.Length; index += 2)
                {
                    if (index + 1 >= args.Length) throw new RuntimeFailureException("invalid_arguments", args[index] + " 缺少参数值。");
                    if (args[index] == "--runtime-dir") result.RuntimeDirectory = Path.GetFullPath(args[index + 1]);
                    else if (args[index] == "--arguments") result.ArgumentsFile = Path.GetFullPath(args[index + 1]);
                    else if (args[index] == "--workflow-root") result.WorkflowRoot = Path.GetFullPath(args[index + 1]);
                    else if (args[index] == "--result-file") result.ResultFile = Path.GetFullPath(args[index + 1]);
                    else throw new RuntimeFailureException("invalid_arguments", "未知参数：" + args[index]);
                }
                return result;
            }

            public Dictionary<string, object> LoadArguments()
            {
                if (string.IsNullOrWhiteSpace(ArgumentsFile)) return new Dictionary<string, object>();
                if (!File.Exists(ArgumentsFile)) throw new RuntimeFailureException("arguments_not_found", "参数文件不存在：" + ArgumentsFile);
                return JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(ArgumentsFile))
                    ?? new Dictionary<string, object>();
            }
        }

        private sealed class WorkflowDocument
        {
            public string Name { get; private set; }
            public string ProjectAndName { get; private set; }
            public string Xaml { get; private set; }
            public string Culture { get; private set; }
            public string SourceFile { get; private set; }

            public static WorkflowDocument Load(string path)
            {
                if (!File.Exists(path)) throw new RuntimeFailureException("workflow_not_found", "workflow JSON 不存在：" + path);
                JObject json;
                try { json = JObject.Parse(File.ReadAllText(path)); }
                catch (JsonException ex) { throw new RuntimeFailureException("workflow_json_invalid", "workflow JSON 解析失败：" + ex.Message); }

                string type = (string)json["_type"];
                if (!string.Equals(type, "workflow", StringComparison.OrdinalIgnoreCase))
                    throw new RuntimeFailureException("not_a_workflow", "JSON 的 _type 必须为 workflow。");
                string xaml = (string)(json["Xaml"] ?? json["xaml"]);
                if (string.IsNullOrWhiteSpace(xaml)) throw new RuntimeFailureException("xaml_missing", "workflow JSON 缺少 Xaml。");
                return new WorkflowDocument
                {
                    Name = (string)json["name"] ?? Path.GetFileNameWithoutExtension(path),
                    ProjectAndName = (string)json["projectandname"],
                    Xaml = xaml,
                    Culture = (string)json["culture"],
                    SourceFile = Path.GetFullPath(path)
                };
            }
        }

        private sealed class DependencyReport
        {
            public List<string> RequiredAssemblies { get; private set; }
            public List<string> MissingAssemblies { get; private set; }

            public static DependencyReport Create(string xaml, string runtimeDirectory)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in Regex.Matches(xaml, "assembly\\s*=\\s*([^;\\\"'\\s]+)", RegexOptions.IgnoreCase))
                {
                    names.Add(match.Groups[1].Value.Trim());
                }
                foreach (Match match in Regex.Matches(xaml, @"<AssemblyReference[^>]*>\s*([^<]+?)\s*</AssemblyReference>", RegexOptions.IgnoreCase))
                {
                    string value = match.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(value)) names.Add(value.Trim());
                }

                List<string> required = names.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> missing = new List<string>();
                foreach (string name in required)
                {
                    if (FrameworkAssemblies.Contains(name)) continue;
                    if (FindLoadedAssembly(name) != null) continue;
                    bool exists = CandidateDirectories(runtimeDirectory).Any(directory =>
                        File.Exists(Path.Combine(directory, name + ".dll")) || File.Exists(Path.Combine(directory, name + ".exe")));
                    if (!exists) missing.Add(name);
                }
                return new DependencyReport { RequiredAssemblies = required, MissingAssemblies = missing };
            }
        }

        private sealed class OpenRpaRuntimeBootstrap
        {
            private readonly object _client;
            private readonly List<Type> _extensionTypes;
            private readonly Assembly _openRpaAssembly;

            private OpenRpaRuntimeBootstrap(object client, List<Type> extensionTypes, Assembly openRpaAssembly)
            {
                _client = client;
                _extensionTypes = extensionTypes;
                _openRpaAssembly = openRpaAssembly;
            }

            public static OpenRpaRuntimeBootstrap TryInitialize(
                IReadOnlyCollection<string> requiredAssemblies,
                string runtimeDirectory,
                string workflowRoot,
                string activeWorkflowFile)
            {
                bool needsOpenRpa = requiredAssemblies.Any(name =>
                    string.Equals(name, "OpenRPA", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("OpenRPA.", StringComparison.OrdinalIgnoreCase));
                if (!needsOpenRpa) return null;

                Assembly interfacesAssembly = LoadAssembly("OpenRPA.Interfaces", runtimeDirectory, false);
                Assembly openRpaAssembly = LoadAssembly("OpenRPA", runtimeDirectory, false);
                if (interfacesAssembly == null || openRpaAssembly == null)
                {
                    throw new RuntimeFailureException(
                        "openrpa_runtime_incomplete",
                        "workflow 使用 OpenRPA 活动，但 runtime 目录缺少 OpenRPA.exe 或 OpenRPA.Interfaces.dll。");
                }

                try
                {
                    ConfigureOfflineRuntime(interfacesAssembly);
                    Type robotInstanceType = openRpaAssembly.GetType("OpenRPA.RobotInstance", true, false);
                    object client = robotInstanceType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                    InitializeOpenRpaUiSynchronizationContext(interfacesAssembly);
                    LocalWorkflowRegistryReport registry = RegisterLocalWorkflows(
                        openRpaAssembly,
                        robotInstanceType,
                        client,
                        workflowRoot,
                        activeWorkflowFile);
                    AttachHeadlessMainWindow(openRpaAssembly, interfacesAssembly, robotInstanceType, client);

                    Type pluginsType = interfacesAssembly.GetType("OpenRPA.Interfaces.Plugins", true, false);
                    MethodInfo loadPlugins = pluginsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .First(method => method.Name == "LoadPlugins" && method.GetParameters().Length == 1);
                    loadPlugins.Invoke(null, new[] { client });

                    object rawTypes = pluginsType.GetField("WorkflowExtensionsTypes", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    List<Type> extensionTypes = ((System.Collections.IEnumerable)rawTypes).Cast<Type>().ToList();
                    Write(new RuntimeResponse
                    {
                        success = true,
                        action = "openrpa_runtime_initialized",
                        requiredAssemblies = requiredAssemblies.OrderBy(value => value).ToList(),
                        details = "本地项目 " + registry.ProjectCount + " 个，workflow " + registry.WorkflowCount + " 个；已绑定内置 Chrome selector " + registry.ChromeSelectorConversionCount + " 个，StartProcess " + registry.ChromeLaunchBindingCount + " 个"
                    });
                    return new OpenRpaRuntimeBootstrap(client, extensionTypes, openRpaAssembly);
                }
                catch (Exception ex)
                {
                    Exception actual = Unwrap(ex);
                    throw new RuntimeFailureException("openrpa_bootstrap_failed", "OpenRPA 活动运行环境初始化失败：" + actual.Message);
                }
            }

            private static void InitializeOpenRpaUiSynchronizationContext(Assembly interfacesAssembly)
            {
                // InvokeOpenRPA uses GenericTools.RunUI. In the full OpenRPA
                // desktop app this is installed by the main window; Maxwell owns
                // the WPF dispatcher instead, so install an equivalent context.
                Type automationHelper = interfacesAssembly.GetType("OpenRPA.AutomationHelper", true, false);
                PropertyInfo syncContext = automationHelper.GetProperty("syncContext", BindingFlags.Public | BindingFlags.Static);
                syncContext.SetValue(null,
                    new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher),
                    null);
            }

            private static LocalWorkflowRegistryReport RegisterLocalWorkflows(
                Assembly openRpaAssembly,
                Type robotInstanceType,
                object client,
                string workflowRoot,
                string activeWorkflowFile)
            {
                LocalWorkflowRegistryReport report = new LocalWorkflowRegistryReport();
                if (string.IsNullOrWhiteSpace(workflowRoot) || !Directory.Exists(workflowRoot)) return report;

                Type workflowType = openRpaAssembly.GetType("OpenRPA.Workflow", true, false);
                Type projectType = openRpaAssembly.GetType("OpenRPA.Project", true, false);
                object workflows = robotInstanceType.GetField("Workflows", BindingFlags.Public | BindingFlags.Instance).GetValue(client);
                object projects = robotInstanceType.GetField("Projects", BindingFlags.Public | BindingFlags.Instance).GetValue(client);
                MethodInfo add = workflows.GetType().GetMethod("Add");
                MethodInfo addProject = projects.GetType().GetMethod("Add");
                HashSet<string> projectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> workflowIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> workflowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // WorkflowInstance.Create dereferences Workflow.Project(). Registering
                // projects first is therefore required, not merely cosmetic metadata.
                foreach (string projectFile in OrderRegistryFiles(
                    Directory.EnumerateFiles(workflowRoot, "*.rpaproj", SearchOption.AllDirectories),
                    activeWorkflowFile))
                {
                    try
                    {
                        string json = File.ReadAllText(projectFile);
                        JObject document = JObject.Parse(json);
                        string projectId = ((string)document["_id"] ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(projectId) && !projectIds.Add(projectId))
                        {
                            WriteRegistryDuplicateWarning("project _id", projectId, projectFile);
                            continue;
                        }
                        object localProject = JsonConvert.DeserializeObject(json, projectType);
                        if (localProject != null)
                        {
                            addProject.Invoke(projects, new[] { localProject });
                            report.ProjectCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new RuntimeFailureException("project_registry_failed", "无法登记本地项目 " + Path.GetFileName(projectFile) + "：" + Unwrap(ex).Message);
                    }
                }
                foreach (string jsonFile in OrderRegistryFiles(
                    Directory.EnumerateFiles(workflowRoot, "*.json", SearchOption.AllDirectories),
                    activeWorkflowFile))
                {
                    try
                    {
                        string json = File.ReadAllText(jsonFile);
                        JObject document = JObject.Parse(json);
                        if (!string.Equals((string)document["_type"], "workflow", StringComparison.OrdinalIgnoreCase)) continue;
                        string workflowXaml = (string)document["Xaml"];
                        int legacySelectorConversions;
                        int selectorBindings;
                        int launchBindings;
                        string compatibleXaml = ApplyBundledChromeCompatibility(
                            workflowXaml,
                            out legacySelectorConversions,
                            out selectorBindings,
                            out launchBindings);
                        if (selectorBindings > 0 || launchBindings > 0)
                        {
                            document["Xaml"] = compatibleXaml;
                            json = document.ToString(Formatting.None);
                            report.ChromeSelectorConversionCount += selectorBindings;
                            report.ChromeLaunchBindingCount += launchBindings;
                        }
                        string workflowId = ((string)document["_id"] ?? string.Empty).Trim();
                        string projectAndName = ((string)document["projectandname"] ?? string.Empty)
                            .Replace('/', '\\')
                            .Trim();
                        bool duplicateId = !string.IsNullOrWhiteSpace(workflowId) && workflowIds.Contains(workflowId);
                        bool duplicateName = !string.IsNullOrWhiteSpace(projectAndName) && workflowNames.Contains(projectAndName);
                        if (duplicateId || duplicateName)
                        {
                            WriteRegistryDuplicateWarning(
                                duplicateId ? "workflow _id" : "projectandname",
                                duplicateId ? workflowId : projectAndName,
                                jsonFile);
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(workflowId)) workflowIds.Add(workflowId);
                        if (!string.IsNullOrWhiteSpace(projectAndName)) workflowNames.Add(projectAndName);
                        object localWorkflow = JsonConvert.DeserializeObject(json, workflowType);
                        if (localWorkflow != null)
                        {
                            add.Invoke(workflows, new[] { localWorkflow });
                            report.WorkflowCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new RuntimeFailureException("workflow_registry_failed", "无法登记本地 workflow " + Path.GetFileName(jsonFile) + "：" + Unwrap(ex).Message);
                    }
                }
                return report;
            }

            private static IEnumerable<string> OrderRegistryFiles(IEnumerable<string> files, string activeWorkflowFile)
            {
                string activeFile = Path.GetFullPath(activeWorkflowFile);
                string activeDirectory = FindActiveProjectDirectory(activeFile);
                string activePrefix = activeDirectory
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                return files
                    .OrderBy(file =>
                    {
                        string fullPath = Path.GetFullPath(file);
                        if (string.Equals(fullPath, activeFile, StringComparison.OrdinalIgnoreCase)) return 0;
                        return fullPath.StartsWith(activePrefix, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    })
                    .ThenBy(file => file, StringComparer.OrdinalIgnoreCase);
            }

            private static string FindActiveProjectDirectory(string activeWorkflowFile)
            {
                DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(activeWorkflowFile) ?? string.Empty);
                while (directory != null)
                {
                    if (directory.EnumerateFiles("*.rpaproj", SearchOption.TopDirectoryOnly).Any())
                    {
                        return directory.FullName;
                    }
                    directory = directory.Parent;
                }
                return Path.GetDirectoryName(activeWorkflowFile) ?? string.Empty;
            }

            private static void WriteRegistryDuplicateWarning(string kind, string key, string ignoredFile)
            {
                Write(new RuntimeResponse
                {
                    success = true,
                    action = "registry_warning",
                    errorCode = "duplicate_local_workflow",
                    error = "检测到重复的 " + kind + "，已保留当前项目优先版本并忽略：" + ignoredFile,
                    details = "重复键：" + key
                });
            }

            private static void AttachHeadlessMainWindow(Assembly openRpaAssembly, Assembly interfacesAssembly, Type robotInstanceType, object client)
            {
                HeadlessMainWindow window = new HeadlessMainWindow(openRpaAssembly);
                robotInstanceType.GetProperty("Window", BindingFlags.Public | BindingFlags.Instance).SetValue(client, window, null);
            }

            public void AddWorkflowExtensions(WorkflowApplication application)
            {
                foreach (Type extensionType in _extensionTypes)
                {
                    try
                    {
                        object extension = Activator.CreateInstance(extensionType);
                        MethodInfo initialize = extensionType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance);
                        initialize?.Invoke(extension, new[] { _client, null, null });
                        application.Extensions.Add(extension);
                    }
                    catch (Exception ex)
                    {
                        Exception actual = Unwrap(ex);
                        Write(new RuntimeResponse
                        {
                            success = false,
                            action = "extension_warning",
                            errorCode = "workflow_extension_failed",
                            error = extensionType.FullName + " 初始化失败：" + actual.Message
                        });
                    }
                }
            }

            public void RegisterRootWorkflowExecution(Guid workflowApplicationId)
            {
                // InvokeOpenRPA expects every caller to be represented by an
                // OpenRPA WorkflowInstance. The standalone host uses a raw
                // WorkflowApplication for its root, so provide the minimal
                // caller record needed for the child instance's nesting level.
                Type instanceType = _openRpaAssembly.GetType("OpenRPA.WorkflowInstance", true, false);
                object rootInstance = Activator.CreateInstance(instanceType);
                instanceType.GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance)
                    .SetValue(rootInstance, workflowApplicationId.ToString(), null);
                instanceType.GetProperty("ident", BindingFlags.Public | BindingFlags.Instance)
                    .SetValue(rootInstance, 0, null);
                ((IList)instanceType.GetProperty("Instances", BindingFlags.Public | BindingFlags.Static)
                    .GetValue(null, null)).Add(rootInstance);
            }

            private static void ConfigureOfflineRuntime(Assembly interfacesAssembly)
            {
                Type configType = interfacesAssembly.GetType("OpenRPA.Config", true, false);
                object config = configType.GetProperty("local", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                SetProperty(config, "disable_instance_store", true);
                SetProperty(config, "skip_online_state", true);
                SetProperty(config, "skip_child_session_check", true);
                SetProperty(config, "restore_dependencies_on_startup", false);
                SetProperty(config, "doupdatecheck", false);
                SetProperty(config, "enable_analytics", false);
                SetProperty(config, "wsurl", string.Empty);
            }

            private static void SetProperty(object target, string name, object value)
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite) property.SetValue(target, value, null);
            }

            private sealed class LocalWorkflowRegistryReport
            {
                public int ProjectCount { get; set; }
                public int WorkflowCount { get; set; }
                public int ChromeSelectorConversionCount { get; set; }
                public int ChromeLaunchBindingCount { get; set; }
            }
        }

        // InvokeOpenRPA normally sends child completion notifications to the
        // OpenRPA desktop window. Maxwell deliberately has no OpenRPA window, so
        // this minimal proxy performs only the required bookmark hand-off.
        private sealed class HeadlessMainWindow : OpenRPA.Interfaces.IMainWindow
        {
            private readonly Assembly _openRpaAssembly;

            public HeadlessMainWindow(Assembly openRpaAssembly)
            {
                _openRpaAssembly = openRpaAssembly;
            }

            public event OpenRPA.Interfaces.ReadyForActionEventHandler ReadyForAction
            {
                add { }
                remove { }
            }
            public event OpenRPA.Interfaces.StatusEventHandler Status
            {
                add { }
                remove { }
            }
            public bool VisualTracking { get; set; }
            public bool SlowMotion { get; set; }
            public bool IsLoading { get; set; }
            public object SelectedContent { get { return null; } }
            public OpenRPA.Interfaces.IDesigner[] Designers { get { return new OpenRPA.Interfaces.IDesigner[0]; } }
            public OpenRPA.Interfaces.IDesigner Designer { get { return null; } }
            public OpenRPA.Interfaces.IDesigner LastDesigner { get { return null; } }
            public void OnOpenWorkflow(OpenRPA.Interfaces.IWorkflow workflow) { }
            public void OnDetector(OpenRPA.Interfaces.IDetectorPlugin plugin, OpenRPA.Interfaces.IDetectorEvent detector, EventArgs e) { }
            public void MainWindow_WebSocketClient_OnOpen() { }
            public void SetStatus(string message) { }
            public void Hide() { }
            public void Show() { }
            public void Close() { }
            public void OnOpen(object item) { }

            public void IdleOrComplete(OpenRPA.Interfaces.IWorkflowInstance instance, EventArgs e)
            {
                if (instance == null) return;
                if (!instance.isCompleted && !instance.hasError)
                {
                    Write(new RuntimeResponse
                    {
                        success = true,
                        action = "subworkflow_idle",
                        workflowName = instance.name,
                        details = "state=" + instance.state + "; completed=false; parent remains waiting"
                    });
                    return;
                }

                Write(new RuntimeResponse
                {
                    success = !instance.hasError,
                    action = "subworkflow_finished",
                    workflowName = instance.name,
                    error = instance.errormessage,
                    details = "state=" + instance.state + "; completed=" + instance.isCompleted
                });
                ResumeCallingWorkflow(instance);
            }

            private void ResumeCallingWorkflow(object completedChild)
            {
                try
                {
                    Type instanceType = _openRpaAssembly.GetType("OpenRPA.WorkflowInstance", true, false);
                    object instances = instanceType.GetProperty("Instances", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                    string childId = (string)completedChild.GetType().GetProperty("_id", BindingFlags.Public | BindingFlags.Instance).GetValue(completedChild, null);
                    if (string.IsNullOrWhiteSpace(childId)) return;

                    // The parent is hosted by Maxwell's raw WorkflowApplication,
                    // not by OpenRPA.WorkflowInstance. Resume its bookmark first;
                    // the legacy instance loop below remains useful for nested
                    // OpenRPA-created workflows.
                    if (_application != null)
                    {
                        BookmarkResumptionResult resumed = _application.ResumeBookmark(childId, completedChild);
                        if (resumed == BookmarkResumptionResult.Success) return;
                    }

                    foreach (object candidate in ((IEnumerable)instances).Cast<object>().ToList())
                    {
                        object bookmarks = candidate.GetType().GetProperty("Bookmarks", BindingFlags.Public | BindingFlags.Instance).GetValue(candidate, null);
                        if (!(bookmarks is IDictionary dictionary) || !dictionary.Contains(childId)) continue;
                        candidate.GetType().GetMethod("ResumeBookmark", BindingFlags.Public | BindingFlags.Instance)
                            .Invoke(candidate, new object[] { childId, completedChild, true });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Write(new RuntimeResponse
                    {
                        success = false,
                        action = "subworkflow_callback_warning",
                        errorCode = "subworkflow_callback_failed",
                        error = Unwrap(ex).Message
                    });
                }
            }
        }

        private sealed class RuntimeResponse
        {
            public bool success { get; set; }
            public string action { get; set; }
            public string errorCode { get; set; }
            public string error { get; set; }
            public string details { get; set; }
            public string workflowName { get; set; }
            public List<string> requiredAssemblies { get; set; }
            public List<string> missingAssemblies { get; set; }
            public List<string> outputKeys { get; set; }
            public Dictionary<string, object> outputs { get; set; }
            public List<string> bookmarks { get; set; }
        }

        private sealed class RuntimeFailureException : Exception
        {
            public RuntimeFailureException(string code, string message) : base(message) { Code = code; }
            public string Code { get; }
        }
    }
}
