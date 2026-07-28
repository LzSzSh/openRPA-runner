namespace OpenRpaWorkflowLauncher;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            ShowStartupError(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteCrashLog(exception);
            }
        };

        try
        {
            Views.MainWindow window = new();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            ShowStartupError(exception);
            Shutdown(1);
        }
    }

    private static void ShowStartupError(Exception exception)
    {
        WriteCrashLog(exception);
        System.Windows.MessageBox.Show(
            exception.ToString(),
            "Maxwell麦威数字员工启动失败",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = System.IO.Path.Combine(appData, "OpenRpaWorkflowLauncher");
            System.IO.Directory.CreateDirectory(appFolder);
            string logPath = System.IO.Path.Combine(appFolder, "crash.log");
            string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            System.IO.File.AppendAllText(logPath, text);
        }
        catch
        {
            // Ignore logging failures while reporting startup errors.
        }
    }
}
