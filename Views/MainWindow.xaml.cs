using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using OpenRpaWorkflowLauncher.ViewModels;

namespace OpenRpaWorkflowLauncher.Views;

public partial class MainWindow : System.Windows.Window
{
    private const int WmHotKey = 0x0312;
    private const int RunHotkeyId = 0x2001;
    private const int StopHotkeyId = 0x2002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (ViewModel.Logs is INotifyCollectionChanged logs)
            {
                logs.CollectionChanged += OnLogsCollectionChanged;
            }
        }

        _hwndSource = System.Windows.PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
        RegisterGlobalHotkeys();
        ScrollLogsToBottom();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (ViewModel.Logs is INotifyCollectionChanged logs)
            {
                logs.CollectionChanged -= OnLogsCollectionChanged;
            }
        }

        UnregisterGlobalHotkeys();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RunHotkey) ||
            e.PropertyName == nameof(MainViewModel.StopHotkey))
        {
            RegisterGlobalHotkeys();
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollLogsToBottom();
    }

    private void ScrollLogsToBottom()
    {
        LogsListBox.ScrollIntoView(LogsListBox.Items.Count > 0 ? LogsListBox.Items[^1] : null);
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsHotkeyEditor(e.OriginalSource))
        {
            return;
        }

        string? keyText = GetKeyText(e);
        if (keyText is null)
        {
            return;
        }

        if (_hwndSource is not null &&
            (string.Equals(ViewModel?.StopHotkey, keyText, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(ViewModel?.RunHotkey, keyText, StringComparison.OrdinalIgnoreCase)))
        {
            e.Handled = true;
            return;
        }

        if (string.Equals(ViewModel?.StopHotkey, keyText, StringComparison.OrdinalIgnoreCase))
        {
            if (ViewModel is not null)
            {
                await ViewModel.TryStopShortcutAsync(keyText);
            }
            e.Handled = true;
            return;
        }

        if (string.Equals(ViewModel?.RunHotkey, keyText, StringComparison.OrdinalIgnoreCase))
        {
            if (ViewModel is not null)
            {
                await ViewModel.TryRunShortcutAsync(keyText);
            }

            e.Handled = true;
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        string? keyText = GetKeyText(e);
        if (keyText is null || sender is not System.Windows.FrameworkElement element)
        {
            return;
        }

        string? target = element.Tag as string;
        if (target is null)
        {
            return;
        }

        ViewModel?.TrySetHotkey(target, keyText);
    }

    private static bool IsHotkeyEditor(object? source)
    {
        if (source is not System.Windows.DependencyObject dependencyObject)
        {
            return false;
        }

        System.Windows.DependencyObject? current = dependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.FrameworkElement element &&
                element.Tag is string tag &&
                (tag == "Run" || tag == "Stop"))
            {
                return true;
            }

            current = GetParent(current);
        }

        return false;
    }

    private static System.Windows.DependencyObject? GetParent(System.Windows.DependencyObject dependencyObject)
    {
        if (dependencyObject is System.Windows.FrameworkContentElement contentElement)
        {
            return contentElement.Parent;
        }

        if (dependencyObject is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
        {
            return System.Windows.Media.VisualTreeHelper.GetParent(dependencyObject);
        }

        if (dependencyObject is System.Windows.FrameworkElement frameworkElement)
        {
            return frameworkElement.Parent;
        }

        return null;
    }

    private static string? GetKeyText(System.Windows.Input.KeyEventArgs e)
    {
        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        if (key is System.Windows.Input.Key.None
            or System.Windows.Input.Key.LeftShift
            or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftCtrl
            or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt
            or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin
            or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.Clear
            or System.Windows.Input.Key.OemClear)
        {
            return null;
        }

        return key.ToString();
    }

    private void RegisterGlobalHotkeys()
    {
        if (_hwndSource is null || ViewModel is null)
        {
            return;
        }

        UnregisterGlobalHotkeys();

        RegisterHotkey(RunHotkeyId, ViewModel.RunHotkey);
        RegisterHotkey(StopHotkeyId, ViewModel.StopHotkey);
    }

    private void RegisterHotkey(int id, string? hotkey)
    {
        if (_hwndSource is null || string.IsNullOrWhiteSpace(hotkey))
        {
            return;
        }

        if (!Enum.TryParse(hotkey, ignoreCase: true, out System.Windows.Input.Key key))
        {
            return;
        }

        uint virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return;
        }

        RegisterHotKey(_hwndSource.Handle, id, 0, virtualKey);
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_hwndSource is null)
        {
            return;
        }

        UnregisterHotKey(_hwndSource.Handle, RunHotkeyId);
        UnregisterHotKey(_hwndSource.Handle, StopHotkeyId);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmHotKey || ViewModel is null)
        {
            return nint.Zero;
        }

        if (IsHotkeyEditor(System.Windows.Input.Keyboard.FocusedElement))
        {
            return nint.Zero;
        }

        int hotkeyId = wParam.ToInt32();
        _ = hotkeyId switch
        {
            RunHotkeyId => ViewModel.TryRunShortcutAsync(ViewModel.RunHotkey ?? string.Empty),
            StopHotkeyId => ViewModel.TryStopShortcutAsync(ViewModel.StopHotkey ?? string.Empty),
            _ => Task.CompletedTask
        };

        handled = hotkeyId is RunHotkeyId or StopHotkeyId;
        return nint.Zero;
    }
}
