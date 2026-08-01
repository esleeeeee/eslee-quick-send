namespace Eslee.QuickSend.Core.Transfers;

public sealed class CheckpointPolicy(
    long byteThreshold = Protocol.ProtocolConstants.CheckpointBytes,
    TimeSpan? timeThreshold = null)
{
    private readonly TimeSpan _timeThreshold = timeThreshold ?? Protocol.ProtocolConstants.CheckpointInterval;
    private long _lastOffset;
    private DateTimeOffset _lastAt = DateTimeOffset.UtcNow;

    public bool IsDue(long currentOffset, DateTimeOffset now) =>
        currentOffset - _lastOffset >= byteThreshold || now - _lastAt >= _timeThreshold;

    public void MarkCommitted(long offset, DateTimeOffset now)
    {
        if (offset < _lastOffset)
            throw new ArgumentOutOfRangeException(nameof(offset), "Checkpoint offsets cannot move backwards.");
        _lastOffset = offset;
        _lastAt = now;
    }

    public void Restore(long offset, DateTimeOffset at)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        _lastOffset = offset;
        _lastAt = at;
    }
}

