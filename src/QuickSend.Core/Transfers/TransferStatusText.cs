namespace Eslee.QuickSend.Core.Transfers;

/// <summary>
/// Maps an internal transfer state onto the wording the user sees.
/// </summary>
/// <remarks>
/// The database enum and the protocol are unchanged; only presentation lives here.
/// "대기열" is reserved for a job that is genuinely waiting for an earlier job to finish,
/// so it never blurs together with waiting for a device, recovering, or being paused.
/// </remarks>
public static class TransferStatusText
{
    public const string Transferring = "전송 중";
    public const string QueuedPrefix = "대기열";
    public const string WaitingDevice = "기기 연결 대기";
    public const string Recovering = "연결 복구 중";
    public const string Paused = "일시정지";
    public const string ManuallyDisconnected = "연결 끊김";
    public const string Completed = "완료";
    public const string Cancelled = "취소됨";
    public const string Failed = "실패";
    public const string UserActionRequired = "확인 필요";
    public const string ResumeExpired = "이어받기 만료";

    /// <summary>
    /// <paramref name="queuePosition"/> is 1-based and set only while the job is behind
    /// another outgoing job. A queued job whose peer is not reachable is reported as
    /// waiting for the device instead, because its turn is not what is blocking it.
    /// </summary>
    public static string ForJob(
        TransferState state,
        string? errorCode,
        int? queuePosition = null,
        bool destinationOnline = true)
    {
        if (queuePosition is { } position)
            return destinationOnline ? $"{QueuedPrefix} {position}번째" : WaitingDevice;

        return state switch
        {
            TransferState.Transferring or TransferState.Verifying => Transferring,
            // Attempt 0 of a job that already owns its turn is part of starting the transfer.
            TransferState.Connecting or TransferState.Pairing or TransferState.Discovering => Transferring,
            TransferState.Recovering or TransferState.Retrying => Recovering,
            TransferState.WaitingDevice or TransferState.WaitingCondition or TransferState.Queued => WaitingDevice,
            TransferState.Paused when errorCode == TransferHistoryPolicy.ManualDisconnectErrorCode => ManuallyDisconnected,
            TransferState.Paused => Paused,
            TransferState.Completed => Completed,
            TransferState.Cancelled => Cancelled,
            TransferState.FailedFatal => Failed,
            TransferState.UserActionRequired when errorCode == TransferHistoryPolicy.ResumeExpiredErrorCode => ResumeExpired,
            TransferState.UserActionRequired => UserActionRequired,
            _ => Transferring
        };
    }
}
