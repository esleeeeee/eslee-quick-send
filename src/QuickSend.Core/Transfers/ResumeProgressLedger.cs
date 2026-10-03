using Eslee.QuickSend.Core.Protocol;

namespace Eslee.QuickSend.Core.Transfers;

public sealed class ResumeProgressLedger
{
    private readonly Dictionary<Guid, long> _committedOffsets = [];

    public long ObserveResume(Guid fileId, long committedOffset)
    {
        if (committedOffset < 0) throw new ProtocolException("Receiver resume offset cannot be negative.");
        // A new authenticated session may have repaired corrupt durable bytes.
        // Checkpoints within that session must still be monotonic.
        _committedOffsets[fileId] = committedOffset;
        return committedOffset;
    }

    public long ObserveCheckpoint(Guid fileId, long committedOffset) =>
        Observe(fileId, committedOffset, "Receiver checkpoint");

    public long GetCommittedOffset(Guid fileId) =>
        _committedOffsets.GetValueOrDefault(fileId);

    public static long OverallCommitted(long completedBefore, long fileCommittedOffset)
    {
        if (completedBefore < 0 || fileCommittedOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(fileCommittedOffset));
        return checked(completedBefore + fileCommittedOffset);
    }

    private long Observe(Guid fileId, long committedOffset, string source)
    {
        if (committedOffset < 0)
            throw new ProtocolException($"{source} offset cannot be negative.");
        if (_committedOffsets.TryGetValue(fileId, out var previous) && committedOffset < previous)
            throw new ProtocolException(
                $"{source} moved backwards for {fileId}: previously committed {previous}, now {committedOffset}.");
        _committedOffsets[fileId] = committedOffset;
        return committedOffset;
    }
}
