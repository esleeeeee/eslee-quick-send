using System.Buffers.Binary;
using Eslee.QuickSend.Core.Integrity;
using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Protocol;
using Eslee.QuickSend.Core.Storage;

namespace Eslee.QuickSend.Core.Transfers;

public sealed class ReceiverSession : IAsyncDisposable
{
    private readonly ITransferStore _store;
    private readonly CheckpointPolicy _checkpointPolicy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TransferFileRecord _record;
    private FileStream? _partial;
    private MerkleAccumulator _merkle;
    private bool _completed;

    public ReceiverSession(TransferFileRecord record, ITransferStore store, CheckpointPolicy? checkpointPolicy = null)
    {
        if (record.PartialPath is null)
            throw new ArgumentException("A receiver record requires a partial path.", nameof(record));
        if (record.ChunkSize <= 0 || record.ChunkSize > (long)ProtocolConstants.MaxChunkData)
            throw new ArgumentOutOfRangeException(nameof(record), "Invalid chunk size.");
        _record = record;
        _store = store;
        _checkpointPolicy = checkpointPolicy ?? new CheckpointPolicy();
        _merkle = MerkleAccumulator.ImportLeaves(record.MerkleLeaves);
    }

    public long ReceivedOffset => _record.ReceivedOffset;
    public long CommittedOffset => _record.CommittedOffset;

    public ResumeInfoMessage CreateResumeInfo() => new(
        _record.TransferId,
        _record.FileId,
        _record.CommittedOffset,
        _merkle.LeafCount,
        Convert.ToBase64String(_merkle.ExportLeaves()));

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_partial is not null)
                return;
            var path = _record.PartialPath!;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new IOException("Partial path has no parent directory."));
            _partial = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, _record.ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var safe = Math.Min(_record.CommittedOffset, _partial.Length);
            if (safe != _record.Size)
                safe -= safe % _record.ChunkSize;
            var safeLeaves = checked((int)((safe + _record.ChunkSize - 1) / _record.ChunkSize));
            if (safeLeaves < _merkle.LeafCount)
            {
                var bytes = _record.MerkleLeaves.AsSpan(0, safeLeaves * 32).ToArray();
                _merkle = MerkleAccumulator.ImportLeaves(bytes);
            }
            else if (safeLeaves > _merkle.LeafCount)
            {
                safe = Math.Min(_record.Size, (long)_merkle.LeafCount * _record.ChunkSize);
            }

            if (_partial.Length != safe)
            {
                _partial.SetLength(safe);
                _partial.Flush(flushToDisk: true);
            }
            _partial.Position = safe;
            _record = _record with { ReceivedOffset = safe, CommittedOffset = safe, MerkleLeaves = _merkle.ExportLeaves() };
            _checkpointPolicy.Restore(safe, DateTimeOffset.UtcNow);
            await _store.SaveCheckpointAsync(_record.FileId, safe, _record.MerkleLeaves, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ReceiveChunkResult> ReceiveChunkAsync(ReadOnlyMemory<byte> rawPayload, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            Guid fileId;
            long offset;
            int length;
            {
                var chunk = new ChunkPayload(rawPayload.Span);
                fileId = chunk.FileId;
                offset = chunk.Offset;
                length = chunk.Length;
                if (!chunk.VerifyHash())
                    throw new ChunkIntegrityException(_record.FileId, offset);
            }
            if (fileId != _record.FileId)
                throw new ProtocolException("Chunk belongs to a different file.");
            if (offset < 0 || offset + length > _record.Size)
                throw new ProtocolException("Chunk range is outside the declared file size.");
            if (offset < _record.ReceivedOffset)
            {
                if (offset + length <= _record.CommittedOffset)
                    return new ReceiveChunkResult(new ChunkAckMessage(_record.FileId, offset, length, _record.ReceivedOffset), null, Duplicate: true);
                throw new ProtocolException("Chunk overlaps uncommitted receiver data.");
            }
            if (offset != _record.ReceivedOffset)
                throw new ProtocolException($"Chunk gap: expected {_record.ReceivedOffset}, received {offset}.");

            var data = rawPayload[ProtocolConstants.ChunkMetadataSize..];
            await _partial!.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            _merkle.AddChunk(data.Span);
            var received = checked(offset + length);
            _record = _record with { ReceivedOffset = received, MerkleLeaves = _merkle.ExportLeaves() };

            CheckpointMessage? checkpoint = null;
            if (_checkpointPolicy.IsDue(received, now) || received == _record.Size)
                checkpoint = await CommitUnlockedAsync(now, cancellationToken).ConfigureAwait(false);

            return new ReceiveChunkResult(
                new ChunkAckMessage(_record.FileId, offset, length, received),
                checkpoint,
                Duplicate: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<string> CompleteAsync(FileCompleteMessage complete, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (complete.FileId != _record.FileId || complete.Size != _record.Size)
                throw new ProtocolException("File completion metadata does not match the active file.");
            if (_record.ReceivedOffset != _record.Size)
                throw new ProtocolException("File completion arrived before all bytes were received.");
            if (_record.CommittedOffset != _record.Size)
                await CommitUnlockedAsync(now, cancellationToken).ConfigureAwait(false);

            var root = _merkle.ComputeRoot();
            var expected = Convert.FromBase64String(complete.MerkleRootBase64);
            if (complete.LeafCount != _merkle.LeafCount || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(root, expected))
                throw new FileIntegrityException(_record.FileId);

            _partial!.Flush(flushToDisk: true);
            await _partial.DisposeAsync().ConfigureAwait(false);
            _partial = null;

            var desired = _record.FinalPath ?? throw new IOException("Final path is not configured.");
            Directory.CreateDirectory(Path.GetDirectoryName(desired) ?? throw new IOException("Final path has no parent directory."));
            var finalPath = SafePath.ChooseNonConflictingPath(desired);
            File.Move(_record.PartialPath!, finalPath);
            await _store.MarkFileCompletedAsync(_record.FileId, finalPath, root, now, cancellationToken).ConfigureAwait(false);
            _record = _record with { FinalPath = finalPath, State = TransferState.Completed };
            _completed = true;
            return finalPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<CheckpointMessage> CommitUnlockedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _partial!.Flush(flushToDisk: true);
        var leaves = _merkle.ExportLeaves();
        var committed = _record.ReceivedOffset;
        await _store.SaveCheckpointAsync(_record.FileId, committed, leaves, now, cancellationToken).ConfigureAwait(false);
        _record = _record with { CommittedOffset = committed, MerkleLeaves = leaves };
        _checkpointPolicy.MarkCommitted(committed, now);
        return new CheckpointMessage(_record.FileId, committed, _merkle.LeafCount, Convert.ToBase64String(leaves));
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (_partial is null)
            throw new InvalidOperationException("InitializeAsync must be called before receiving data.");
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_partial is not null)
                await _partial.DisposeAsync().ConfigureAwait(false);
            _partial = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

public sealed record ReceiveChunkResult(ChunkAckMessage Ack, CheckpointMessage? Checkpoint, bool Duplicate);

public sealed class ChunkIntegrityException(Guid fileId, long offset)
    : IOException($"Chunk integrity verification failed for {fileId} at offset {offset}.")
{
    public Guid FileId { get; } = fileId;
    public long Offset { get; } = offset;
}

public sealed class FileIntegrityException(Guid fileId)
    : IOException($"Final integrity verification failed for {fileId}.")
{
    public Guid FileId { get; } = fileId;
}
