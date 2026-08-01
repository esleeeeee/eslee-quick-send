using System.Runtime.InteropServices;

namespace Eslee.QuickSend.Windows.Transfers;

public sealed class PowerRequestService : IDisposable
{
    private int _references;

    public IDisposable Acquire()
    {
        if (Interlocked.Increment(ref _references) == 1)
            SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.AwayModeRequired);
        return new Lease(this);
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
            SetThreadExecutionState(ExecutionState.Continuous);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _references, 0);
        SetThreadExecutionState(ExecutionState.Continuous);
    }

    private sealed class Lease(PowerRequestService owner) : IDisposable
    {
        private PowerRequestService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        AwayModeRequired = 0x00000040,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);
}

