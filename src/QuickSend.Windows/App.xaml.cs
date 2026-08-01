using Eslee.QuickSend.Windows.Startup;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Eslee.QuickSend.Windows;

public partial class App : Microsoft.UI.Xaml.Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SingleInstanceGuard _instance = new();
    private MainWindow? _window;
    private Task? _initializationTask;
    private int _exitStarted;

    public App()
    {
        InitializeComponent();
        AppServices.Log.Info("app.process.start", new { processId = Environment.ProcessId });
        UnhandledException += (_, args) =>
        {
            AppServices.Log.Error("app.unhandled", args.Exception);
            args.Handled = false;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Log.Info("app.on_launched.enter");
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null)
        {
            await ExitAfterFatalStartupAsync(new InvalidOperationException("WinUI UI DispatcherQueue is unavailable."));
            return;
        }
        AppServices.Log.Info("app.ui_dispatcher.captured", new
        {
            dispatcher.HasThreadAccess,
            threadId = Environment.CurrentManagedThreadId
        });

        // A second launch must not build another listener, tray icon or database owner.
        if (!_instance.TryAcquire())
        {
            var signalled = SingleInstanceGuard.SignalExistingInstance();
            AppServices.Log.Info("app.single_instance.secondary_exit", new { signalled });
            Exit();
            return;
        }
        _instance.ActivationRequested += (_, _) => _window?.ShowAndActivate("single-instance-activation");

        var startedByAutoStart = AutoStartService.LaunchedByAutoStart(Environment.GetCommandLineArgs());
        AppServices.Log.Info("app.launch.mode", new { startedByAutoStart });

        try
        {
            _window = new MainWindow(dispatcher);
            AppServices.Log.Info("app.main_window.created");
            if (startedByAutoStart)
            {
                // Sign-in start goes straight to the tray: the listener and discovery run,
                // but no window is pushed in front of the user on every boot.
                AppServices.Log.Info("app.main_window.startup_hidden");
            }
            else
            {
                _window.Activate();
                AppServices.Log.Info("app.main_window.activate.called");
                _window.ShowAndActivate("initial-launch");
            }
        }
        catch (Exception ex)
        {
            await ExitAfterFatalStartupAsync(ex);
            return;
        }

        _initializationTask = Task.Run(() => AppServices.InitializeAsync(_lifetime.Token));
        try
        {
            await _initializationTask;
            if (_window is not null)
                await _window.OnServicesReadyAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            AppServices.Log.Info("app.initialization.cancelled");
        }
        catch (Exception ex)
        {
            await AppServices.Log.ErrorAsync("app.initialization.failed", ex);
            _window?.ShowInitializationError(ex);
        }
        finally
        {
            await AppServices.Log.InfoAsync("app.on_launched.exit", new { windowExists = _window is not null });
        }
    }

    internal async Task RequestExitFromTrayAsync()
    {
        await AppServices.Log.InfoAsync("app.exit.requested", new { source = "tray" });
        if (_window is null)
        {
            await CompleteExitAsync();
            return;
        }
        _window.AllowClose();
        _window.Close();
    }

    internal async void OnMainWindowClosed(MainWindow window)
    {
        if (!ReferenceEquals(_window, window)) return;
        _window = null;
        await CompleteExitAsync();
    }

    private async Task CompleteExitAsync()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;
        await AppServices.Log.InfoAsync("app.exit.start");
        _lifetime.Cancel();
        if (_initializationTask is not null)
        {
            try { await _initializationTask; }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { await AppServices.Log.ErrorAsync("app.exit.initialization_wait.failed", ex); }
        }
        await AppServices.ShutdownAsync();
        _instance.Dispose();
        await AppServices.Log.InfoAsync("app.exit.complete");
        Exit();
    }

    private async Task ExitAfterFatalStartupAsync(Exception exception)
    {
        await AppServices.Log.ErrorAsync("app.startup.fatal", exception);
        await CompleteExitAsync();
    }
}
