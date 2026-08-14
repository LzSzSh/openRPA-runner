using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Maxwell.NotificationHost
{
    internal static class Program
    {
        private const string PipeNamePrefix = "maxwell-notification-host-v2-";
        private const string MutexNamePrefix = "Local\\Maxwell.NotificationHost.v2.";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 2) return 2;

            string message;
            try
            {
                message = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            }
            catch (FormatException)
            {
                return 3;
            }

            int ownerProcessId = ReadOwnerProcessId();
            string ownerKey = ownerProcessId > 0 ? ownerProcessId.ToString() : "legacy";
            string pipeName = PipeNamePrefix + ownerKey;
            string mutexName = MutexNamePrefix + ownerKey;

            // The normal path: hand this notification to the already-running
            // manager, which owns the screen positions for every toast.
            if (TrySend(pipeName, args[0], message, 250)) return 0;

            bool createdNew;
            using (var mutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // A manager is starting up. Give its pipe a moment to open
                    // rather than starting a second overlapping window.
                    return TrySend(pipeName, args[0], message, 2000) ? 0 : 4;
                }

                var application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                var manager = new NotificationManager(application, pipeName, ownerProcessId);
                manager.Start(args[0], message);
                application.Run();
                return 0;
            }
        }

        private static int ReadOwnerProcessId()
        {
            int ownerProcessId;
            return int.TryParse(
                Environment.GetEnvironmentVariable("MAXWELL_OWNER_PROCESS_ID"),
                out ownerProcessId) && ownerProcessId > 0
                ? ownerProcessId
                : 0;
        }

        private static bool TrySend(string pipeName, string notificationType, string message, int timeoutMilliseconds)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                {
                    client.Connect(timeoutMilliseconds);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(notificationType ?? "Information");
                        writer.Write(message ?? string.Empty);
                        writer.Flush();
                    }
                }
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    internal sealed class NotificationManager
    {
        private const double WindowGap = 10;
        private const double ScreenMargin = 12;
        private readonly Application _application;
        private readonly string _pipeName;
        private readonly int _ownerProcessId;
        private readonly List<NotificationWindow> _windows = new List<NotificationWindow>();
        private bool _arrangePending;

        public NotificationManager(Application application, string pipeName, int ownerProcessId)
        {
            _application = application;
            _pipeName = pipeName;
            _ownerProcessId = ownerProcessId;
        }

        public void Start(string notificationType, string message)
        {
            Task.Run((Action)ListenForNotifications);
            if (_ownerProcessId > 0) Task.Run((Action)MonitorOwnerProcess);
            Show(notificationType, message);
        }

        private void MonitorOwnerProcess()
        {
            try
            {
                using (Process owner = Process.GetProcessById(_ownerProcessId))
                {
                    if (!owner.HasExited) owner.WaitForExit();
                }
            }
            catch (ArgumentException)
            {
                // The owner closed before the monitor attached.
            }
            catch (InvalidOperationException)
            {
                // Treat an inaccessible/exited owner as closed.
            }

            _application.Dispatcher.BeginInvoke(new Action(() => _application.Shutdown()));
        }

        private void ListenForNotifications()
        {
            while (true)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string notificationType = reader.ReadLine();
                            string message = reader.ReadToEnd();
                            _application.Dispatcher.BeginInvoke(new Action(() => Show(notificationType, message)));
                        }
                    }
                }
                catch (IOException)
                {
                    // A client can disappear while connecting. Keep serving the
                    // next notification rather than terminating the manager.
                }
            }
        }

        private void Show(string notificationType, string message)
        {
            var window = new NotificationWindow(notificationType, message);
            window.Closed += (sender, eventArgs) =>
            {
                _windows.Remove(window);
                ScheduleArrange();
            };
            // A WPF window does not have its final ActualHeight until it has
            // completed its first layout pass. Arrange from LayoutUpdated
            // rather than using the initial (often zero) height, otherwise
            // notifications created in quick succession can be placed on top
            // of each other.
            window.SizeChanged += (sender, eventArgs) => ScheduleArrange();
            window.ContentRendered += (sender, eventArgs) => ScheduleArrange();
            _windows.Add(window);
            window.Show();
            ScheduleArrange();
        }

        private void ScheduleArrange()
        {
            if (_arrangePending) return;
            _arrangePending = true;
            _application.Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    _arrangePending = false;
                    ArrangeWindows();
                }));
        }

        private void ArrangeWindows()
        {
            Rect workArea = SystemParameters.WorkArea;
            double bottomOffset = ScreenMargin;
            double leftOffset = ScreenMargin;
            foreach (NotificationWindow window in _windows)
            {
                if (!window.IsLoaded) continue;

                double windowHeight = Math.Max(window.ActualHeight, window.MinHeight);
                double windowWidth = Math.Max(window.ActualWidth, window.Width);

                // Keep the newest notification above the preceding one. If a
                // column reaches the top of the work area, continue in a new
                // column to the left instead of covering an existing popup.
                if (bottomOffset + windowHeight > workArea.Height - ScreenMargin && bottomOffset > ScreenMargin)
                {
                    bottomOffset = ScreenMargin;
                    leftOffset += windowWidth + WindowGap;
                }

                window.PlaceAboveBottom(workArea, bottomOffset, leftOffset);
                bottomOffset += windowHeight + WindowGap;
            }
        }
    }

    internal sealed class NotificationWindow : Window
    {
        private readonly DispatcherTimer _lifetimeTimer;

        public NotificationWindow(string notificationType, string message)
        {
            string normalizedType = notificationType ?? "Information";
            Title = "Maxwell";
            Width = 380;
            MinHeight = 116;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = BackgroundFor(normalizedType);

            var root = new Border
            {
                Background = Background,
                BorderBrush = BorderFor(normalizedType),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16)
            };
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textPanel = new StackPanel();
            textPanel.Children.Add(new TextBlock
            {
                Text = TitleFor(normalizedType),
                FontWeight = FontWeights.SemiBold,
                FontSize = 16,
                Foreground = ForegroundFor(normalizedType),
                Margin = new Thickness(0, 0, 12, 8)
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Foreground = ForegroundFor(normalizedType),
                MaxWidth = 300
            });
            Grid.SetColumn(textPanel, 0);
            content.Children.Add(textPanel);

            var closeButton = new Button
            {
                Content = "×",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = ForegroundFor(normalizedType),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 0, 0, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "关闭"
            };
            closeButton.Click += (sender, eventArgs) => Close();
            Grid.SetColumn(closeButton, 1);
            content.Children.Add(closeButton);
            root.Child = content;
            Content = root;

            int lifetimeSeconds = LifetimeSecondsFor(normalizedType);
            if (lifetimeSeconds > 0)
            {
                _lifetimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(lifetimeSeconds) };
                _lifetimeTimer.Tick += (sender, eventArgs) => Close();
                _lifetimeTimer.Start();
                Closed += (sender, eventArgs) => _lifetimeTimer.Stop();
            }
        }

        public void PlaceAboveBottom(Rect workArea, double bottomOffset, double rightOffset)
        {
            Left = workArea.Right - ActualWidth - rightOffset;
            Top = workArea.Bottom - ActualHeight - bottomOffset;
        }

        private static int LifetimeSecondsFor(string notificationType)
        {
            if (string.Equals(notificationType, "Warning", StringComparison.OrdinalIgnoreCase)) return 60;
            if (string.Equals(notificationType, "Success", StringComparison.OrdinalIgnoreCase)) return 30;
            if (string.Equals(notificationType, "Error", StringComparison.OrdinalIgnoreCase)) return 0;
            return 3;
        }

        private static string TitleFor(string notificationType)
        {
            if (string.Equals(notificationType, "Warning", StringComparison.OrdinalIgnoreCase)) return "警告";
            if (string.Equals(notificationType, "Success", StringComparison.OrdinalIgnoreCase)) return "成功";
            if (string.Equals(notificationType, "Error", StringComparison.OrdinalIgnoreCase)) return "错误";
            return "通知";
        }

        private static Brush BackgroundFor(string notificationType)
        {
            if (string.Equals(notificationType, "Warning", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#FFF3CD");
            if (string.Equals(notificationType, "Success", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#D4EDDA");
            if (string.Equals(notificationType, "Error", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#F8D7DA");
            return BrushFrom("#D1ECF1");
        }

        private static Brush BorderFor(string notificationType)
        {
            if (string.Equals(notificationType, "Warning", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#E0A800");
            if (string.Equals(notificationType, "Success", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#28A745");
            if (string.Equals(notificationType, "Error", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#DC3545");
            return BrushFrom("#17A2B8");
        }

        private static Brush ForegroundFor(string notificationType)
        {
            if (string.Equals(notificationType, "Warning", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#856404");
            if (string.Equals(notificationType, "Success", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#155724");
            if (string.Equals(notificationType, "Error", StringComparison.OrdinalIgnoreCase)) return BrushFrom("#721C24");
            return BrushFrom("#0C5460");
        }

        private static Brush BrushFrom(string value)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
    }
}
