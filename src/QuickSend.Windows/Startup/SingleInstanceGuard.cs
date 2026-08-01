namespace Eslee.QuickSend.Windows.Startup;

/// <summary>
/// Keeps one QuickSend process per Windows sign-in session.
/// </summary>
/// <remarks>
/// A second launch must not create another listener, tray icon or database owner. It
/// signals the running instance to show its window and then exits. The names use the
/// <c>Local\</c> prefix so the scope is the current session, matching the per-user install.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\eslee.QuickSend.singleinstance";
    private const string ActivateEventName = @"Local\eslee.QuickSend.activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateSignal;
    private CancellationTokenSource? _listenerLifetime;
    private Task? _listener;

    public bool IsPrimary { get; private set; }

    /// <summary>Raised on a background thread when another launch asks to show the window.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>True when this process owns the instance and should continue starting up.</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimary = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _listenerLifetime = new CancellationTokenSource();
        _listener = Task.Factory.StartNew(
            () => WaitForActivations(_listenerLifetime.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        return true;
    }

    /// <summary>Asks the already-running instance to restore its window.</summary>
    public static bool SignalExistingInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle)) return false;
        using (handle) return handle.Set();
    }

    private void WaitForActivations(CancellationToken cancellationToken)
    {
        var signal = _activateSignal;
        if (signal is null) return;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!signal.WaitOne(500)) continue;
                if (cancellationToken.IsCancellationRequested) return;
                ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _listenerLifetime?.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _listenerLifetime?.Dispose();
        _activateSignal?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _mutex.Dispose();
        }
        _mutex = null;
        _activateSignal = null;
    }
}
