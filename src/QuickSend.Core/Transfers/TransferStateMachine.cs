namespace Eslee.QuickSend.Core.Transfers;

public sealed class TransferStateMachine
{
    private static readonly IReadOnlyDictionary<TransferState, HashSet<TransferState>> Allowed =
        new Dictionary<TransferState, HashSet<TransferState>>
        {
            [TransferState.Queued] = [TransferState.Discovering, TransferState.Connecting, TransferState.Cancelled],
            [TransferState.Discovering] = [TransferState.Connecting, TransferState.WaitingDevice, TransferState.Paused, TransferState.Cancelled],
            [TransferState.Connecting] = [TransferState.Pairing, TransferState.Transferring, TransferState.Recovering, TransferState.WaitingDevice, TransferState.Paused, TransferState.Cancelled, TransferState.UserActionRequired],
            [TransferState.Pairing] = [TransferState.Transferring, TransferState.UserActionRequired, TransferState.Cancelled],
            [TransferState.Transferring] = [TransferState.Recovering, TransferState.Verifying, TransferState.Paused, TransferState.Cancelled, TransferState.WaitingCondition, TransferState.UserActionRequired, TransferState.FailedFatal],
            [TransferState.Recovering] = [TransferState.Retrying, TransferState.WaitingDevice, TransferState.WaitingCondition, TransferState.Paused, TransferState.Cancelled, TransferState.UserActionRequired, TransferState.FailedFatal],
            [TransferState.Retrying] = [TransferState.Connecting, TransferState.Transferring, TransferState.WaitingDevice, TransferState.Paused, TransferState.Cancelled],
            [TransferState.WaitingDevice] = [TransferState.Connecting, TransferState.Retrying, TransferState.Paused, TransferState.Cancelled, TransferState.UserActionRequired],
            [TransferState.WaitingCondition] = [TransferState.Retrying, TransferState.Paused, TransferState.Cancelled, TransferState.UserActionRequired],
            [TransferState.Verifying] = [TransferState.Completed, TransferState.Recovering, TransferState.UserActionRequired, TransferState.FailedFatal, TransferState.Cancelled],
            [TransferState.Paused] = [TransferState.Discovering, TransferState.Connecting, TransferState.Transferring, TransferState.Cancelled],
            [TransferState.UserActionRequired] = [TransferState.Retrying, TransferState.Cancelled, TransferState.FailedFatal],
            [TransferState.Completed] = [],
            [TransferState.Cancelled] = [],
            [TransferState.FailedFatal] = []
        };

    private readonly object _gate = new();
    private TransferState _state;

    public TransferStateMachine(TransferState initial = TransferState.Queued) => _state = initial;

    public TransferState State
    {
        get { lock (_gate) return _state; }
    }

    public bool TryTransition(TransferState expected, TransferState next)
    {
        lock (_gate)
        {
            if (_state != expected || !Allowed[_state].Contains(next))
                return false;
            _state = next;
            return true;
        }
    }

    public void Transition(TransferState next)
    {
        lock (_gate)
        {
            if (!Allowed[_state].Contains(next))
                throw new InvalidOperationException($"Illegal transfer transition: {_state} -> {next}.");
            _state = next;
        }
    }
}

