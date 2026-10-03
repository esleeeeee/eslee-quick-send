using Eslee.QuickSend.Core.Transfers;

namespace Eslee.QuickSend.Core.Persistence;

public enum TransferDirection
{
    Send,
    Receive
}

public sealed record TransferJobRecord(
    Guid TransferId,
    string SourceDeviceId,
    string DestinationDeviceId,
    TransferDirection Direction,
    TransferState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt = null,
    string? ErrorCode = null);

public sealed record TransferFileRecord(
    Guid TransferId,
    Guid FileId,
    string RelativePath,
    string SourceLocation,
    string? PartialPath,
    string? FinalPath,
    long Size,
    long ModifiedUtcTicks,
    string? StableSourceId,
    int ChunkSize,
    long ReceivedOffset,
    long CommittedOffset,
    byte[] MerkleLeaves,
    TransferState State,
    int RetryCount = 0,
    string? ErrorCode = null);

public interface ITransferStore
{
    ValueTask UpsertJobAsync(TransferJobRecord job, CancellationToken cancellationToken);
    ValueTask UpsertFileAsync(TransferFileRecord file, CancellationToken cancellationToken);
    ValueTask SaveCheckpointAsync(Guid fileId, long committedOffset, byte[] merkleLeaves, DateTimeOffset at, CancellationToken cancellationToken);
    ValueTask RestoreCheckpointAsync(Guid fileId, long expectedCommittedOffset, long committedOffset, byte[] merkleLeaves, DateTimeOffset at, CancellationToken cancellationToken);
    ValueTask MarkFileCompletedAsync(Guid fileId, string finalPath, byte[] merkleRoot, DateTimeOffset at, CancellationToken cancellationToken);
    ValueTask<TransferFileRecord?> FindFileAsync(Guid fileId, CancellationToken cancellationToken);
    IAsyncEnumerable<TransferJobRecord> FindRecoverableJobsAsync(CancellationToken cancellationToken);
}
