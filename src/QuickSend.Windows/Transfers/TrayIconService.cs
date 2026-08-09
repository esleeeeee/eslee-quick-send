using System.Runtime.InteropServices;
using System.Windows.Input;
using Eslee.QuickSend.Windows.Diagnostics;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Eslee.QuickSend.Windows.Transfers;

public sealed class TrayIconService : IDisposable
{
    private const uint OpenCommand = 1;
    private const uint PauseCommand = 2;
    private const uint ExitCommand = 3;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const uint WmNull = 0x0000;

    private readonly DiagnosticLog _log;
    private readonly nint _ownerHwnd;
    private readonly DispatcherQueue _uiDispatcher;
    private readonly TaskbarIcon _icon;
    private bool _iconVisible = true;
    private bool _disposed;

    public TrayIconService(DiagnosticLog log, nint ownerHwnd, DispatcherQueue uiDispatcher)
    {
        _log = log;
        _ownerHwnd = ownerHwnd != 0 ? ownerHwnd : throw new ArgumentException("A valid owner HWND is required.", nameof(ownerHwnd));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _log.Info("tray.initialization.start", new { hwnd = $"0x{_ownerHwnd:X}" });

        _icon = new TaskbarIcon
        {
            Id = Guid.Parse("C3B9349A-18BE-48E9-8BF2-44D7820A8190"),
            ToolTipText = "eslee QuickSend",
            LeftClickCommand = new DelegateCommand(() => InvokeOpen("tray.left_click.callback")),
            DoubleClickCommand = new DelegateCommand(() => InvokeOpen("tray.double_click.callback")),
            RightClickCommand = new DelegateCommand(QueueContextMenu),
            IconSource = TryCreateBrandIconSource(log) ?? CreateGeneratedIconSource()
        };

        try
        {
            _icon.ForceCreate(false);
        }
        catch (Exception ex)
        {
            // Never let icon artwork be the reason the tray is missing: retry once with the
            // generated mark, which has no file or decoder dependency.
            _log.Warn("tray.icon.brand.create_failed", new { error = ex.GetType().Name });
            _icon.IconSource = CreateGeneratedIconSource();
            _icon.ForceCreate(false);
        }
        _log.Info("tray.initialization.complete", new { _icon.IsCreated });
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ExitRequested;

    public void ShowTransferContinuesNotice()
    {
        if (!_iconVisible)
        {
            // Tray Folder Hosted 모드로 아이콘이 숨겨진 동안에는 풍선을 표시할 수 없습니다.
            _log.Info("tray.transfer_continues_notice.suppressed_hidden");
            return;
        }

        _log.Info("tray.transfer_continues_notice");
        _icon.ShowNotification(
            "eslee QuickSend",
            "전송은 백그라운드에서 계속됩니다.",
            NotificationIcon.Info);
    }

    /// <summary>
    /// Tray Folder Hosted 모드 전환용 아이콘 표시 제어입니다. 아이콘만 표시/제거되며
    /// 수신 대기와 전송 동작은 그대로 유지됩니다. UI 스레드에서 호출하세요.
    /// </summary>
    public void SetTrayIconVisible(bool visible)
    {
        if (_disposed || visible == _iconVisible) return;
        _log.Info("tray.visibility", new { visible });
        try
        {
            _icon.Visibility = visible
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
            _iconVisible = visible;
        }
        catch (Exception ex)
        {
            _log.Error("tray.visibility.failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Info("tray.dispose.start");
        _icon.Dispose();
        _log.Info("tray.dispose.complete");
    }

    /// <summary>
    /// Uses the arrow-only brand icon, which stays legible at the 16px tray size where the
    /// "eslee" wordmark cannot be read. Returns <c>null</c> when the file is missing so the
    /// caller can fall back rather than leave the user without a tray icon.
    /// </summary>
    private static ImageSource? TryCreateBrandIconSource(DiagnosticLog log)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "eslee-quicksend-tray.ico");
        if (!File.Exists(iconPath))
        {
            log.Warn("tray.icon.brand.missing", new { iconPath });
            return null;
        }

        try
        {
            log.Info("tray.icon.source", new { source = "brand", iconPath });
            return new BitmapImage(new Uri(iconPath));
        }
        catch (Exception ex)
        {
            log.Warn("tray.icon.brand.failed", new { error = ex.GetType().Name });
            return null;
        }
    }

    private static ImageSource CreateGeneratedIconSource() => new GeneratedIconSource
    {
        Text = "Q",
        FontSize = 44,
        Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
        Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 25, 157, 119))
    };

    private void InvokeOpen(string eventName)
    {
        _log.Info(eventName);
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void QueueContextMenu()
    {
        _log.Info("tray.right_click.callback");
        var queued = _uiDispatcher.TryEnqueue(ShowContextMenu);
        _log.Info("ui.dispatch.enqueue", new { operation = "tray-context-menu", queued, source = "tray" });
        if (!queued) _log.Warn("ui.dispatch.enqueue.failed", new { operation = "tray-context-menu", source = "tray" });
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            _log.Warn("tray.menu.create.failed", new { error = Marshal.GetLastWin32Error() });
            return;
        }

        try
        {
            AppendMenu(menu, MfString, OpenCommand, "QuickSend 열기");
            AppendMenu(menu, MfString, PauseCommand, "일시정지 / 계속");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommand, "종료");
            if (!GetCursorPos(out var point))
            {
                _log.Warn("tray.menu.cursor.failed", new { error = Marshal.GetLastWin32Error() });
                return;
            }

            SetForegroundWindow(_ownerHwnd);
            _log.Info("tray.menu.open", new { point.X, point.Y, menuOwner = $"0x{_ownerHwnd:X}" });
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmNonotify | TpmReturnCmd, point.X, point.Y, _ownerHwnd, 0);
            PostMessage(_ownerHwnd, WmNull, 0, 0);

            switch (command)
            {
                case OpenCommand:
                    _log.Info("tray.menu.open.callback");
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case PauseCommand:
                    _log.Info("tray.menu.pause.callback");
                    PauseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case ExitCommand:
                    _log.Info("tray.menu.exit.callback");
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
                default:
                    _log.Info("tray.menu.dismissed");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("tray.menu.failed", ex);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    private sealed class DelegateCommand(Action action) : ICommand
    {
#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
