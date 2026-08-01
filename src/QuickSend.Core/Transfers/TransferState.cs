namespace Eslee.QuickSend.Core.Transfers;

public enum TransferState
{
    Queued,
    Discovering,
    Connecting,
    Pairing,
    Transferring,
    Recovering,
    Retrying,
    WaitingDevice,
    WaitingCondition,
    Verifying,
    Paused,
    Completed,
    Cancelled,
    UserActionRequired,
    FailedFatal
}

