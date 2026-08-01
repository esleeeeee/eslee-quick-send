namespace Eslee.QuickSend.Core.Transfers;

/// <summary>
/// Separates jobs that are genuinely recoverable from rows that only look active
/// because an old attempt never reached a terminal state.
/// </summary>
/// <remarks>
/// Deleting a history row never touches the sent source file or the received file;
/// it only removes the database bookkeeping for a job that is not running.
/// </remarks>
public static class TransferHistoryPolicy
{
    /// <summary>How long an interrupted job stays eligible for automatic resume.</summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromDays(7);

    public const string ResumeExpiredErrorCode = "RESUME_EXPIRED";

    /// <summary>Error code stored when the user, not the network, ended the session.</summary>
    public const string ManualDisconnectErrorCode = "MANUAL_DISCONNECT";

    private static readonly TransferState[] SettledStates =
    [
        TransferState.Completed,
        TransferState.Cancelled,
        TransferState.FailedFatal,
        // The job stopped and is waiting for the user; nothing is running for it.
        TransferState.UserActionRequired
    ];

    /// <summary>States in which no worker owns the job, so its history row is safe to remove.</summary>
    public static bool IsSettled(TransferState state) => Array.IndexOf(SettledStates, state) >= 0;

    public static IReadOnlyList<TransferState> SettledStateList => SettledStates;

    /// <summary>
    /// True when a persisted job should be picked up again by automatic resume.
    /// Settled jobs are never resumed, and an untouched job eventually ages out so a
    /// stale row cannot pretend to be an active transfer forever.
    /// </summary>
    public static bool IsResumable(TransferState state, DateTimeOffset updatedAt, DateTimeOffset now) =>
        !IsSettled(state) && now - updatedAt <= ResumeWindow;

    /// <summary>True when the row is stale enough that resume must be abandoned.</summary>
    public static bool IsResumeExpired(TransferState state, DateTimeOffset updatedAt, DateTimeOffset now) =>
        !IsSettled(state) && now - updatedAt > ResumeWindow;

    /// <summary>
    /// A history row can be deleted when the job is settled, or when nothing is
    /// currently running for it. Live transfers stay protected.
    /// </summary>
    public static bool CanDeleteHistory(TransferState state, bool isRunning) => IsSettled(state) || !isRunning;
}
