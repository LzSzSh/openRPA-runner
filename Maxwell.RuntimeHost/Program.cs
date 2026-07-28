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

        private static string _runtimeDirectory;
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
                _runtimeDirectory = options.RuntimeDirectory;
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

                if (dependencies.RequiredAssemblies.Any(name => string.Equals(name, "OpenRPA.NM", StringComparison.OrdinalIgnoreCase)))
                {
                    EnsureBrowserNativeMessagingRegistration(_runtimeDirectory);
                }

                Dictionary<string, object> inputs = options.LoadArguments();
                return Run(workflow, dependencies, inputs, options.WorkflowRoot);
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
            string hostExecutable = Path.Combine(runtimeDirectory, "OpenRPA.NativeMessagingHost.exe");
            string manifestPath = Path.Combine(runtimeDirectory, "chromemanifest.json");
            string templatePath = Path.Combine(runtimeDirectory, "chromemanifest.template.json");
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
            string escapedHostPath = hostExecutable.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string updatedManifest = Regex.Replace(
                sourceManifest,
                @"(""path""\s*:\s*"")[^""]*("")",
                match => match.Groups[1].Value + escapedHostPath + match.Groups[2].Value,
                RegexOptions.CultureInvariant);

            if (updatedManifest == sourceManifest)
            {
                throw new RuntimeFailureException(
                    "browser_manifest_invalid",
                    "Unable to update the browser Native Messaging manifest: " + manifestPath);
            }

            File.WriteAllText(manifestPath, updatedManifest, new UTF8Encoding(false));
            RegisterBrowserNativeMessagingHost(@"Software\Google\Chrome\NativeMessagingHosts\com.openrpa.msg", manifestPath);
            RegisterBrowserNativeMessagingHost(@"Software\Microsoft\Edge\NativeMessagingHosts\com.openrpa.msg", manifestPath);
        }

        private static void RegisterBrowserNativeMessagingHost(string registryPath, string manifestPath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath))
            {
                key?.SetValue(string.Empty, manifestPath, RegistryValueKind.String);
            }
        }

        private static int Run(WorkflowDocument workflow, DependencyReport dependencies, Dictionary<string, object> inputs, string workflowRoot)
        {
            string failureActivity = null;
            foreach (string assemblyName in dependencies.RequiredAssemblies)
            {
                // Framework assemblies are resolved by .NET Framework itself. Loading
                // facade assemblies such as System.Runtime.WindowsRuntime by partial
                // name is unreliable even though XAML compilation can resolve them.
                if (FrameworkAssemblies.Contains(assemblyName)) continue;
                LoadAssembly(assemblyName, _runtimeDirectory);
            }

            OpenRpaRuntimeBootstrap openRpaBootstrap = OpenRpaRuntimeBootstrap.TryInitialize(
                dependencies.RequiredAssemblies,
                _runtimeDirectory,
                workflowRoot,
                workflow.SourceFile);

            if (!string.IsNullOrWhiteSpace(workflow.Culture))
            {
                CultureInfo culture = new CultureInfo(workflow.Culture);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }

            Assembly localAssembly = FindLoadedAssembly("OpenRPA") ?? Assembly.GetExecutingAssembly();
            XamlXmlReaderSettings readerSettings = new XamlXmlReaderSettings { LocalAssembly = localAssembly };
            Activity activity;
            using (StringReader stringReader = new StringReader(workflow.Xaml))
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
            Console.Out.WriteLine(JsonConvert.SerializeObject(value, Formatting.None));
            Console.Out.Flush();
        }

        private sealed class Options
        {
            public string Command { get; private set; }
            public string WorkflowFile { get; private set; }
            public string RuntimeDirectory { get; private set; }
            public string ArgumentsFile { get; private set; }
            public string WorkflowRoot { get; private set; }

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
                        details = "本地项目 " + registry.ProjectCount + " 个，workflow " + registry.WorkflowCount + " 个"
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
