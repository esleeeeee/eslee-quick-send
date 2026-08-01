using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Eslee.QuickSend.Core.Integrity;
using Eslee.QuickSend.Core.Protocol;

namespace Eslee.QuickSend.Core.Transfers;

public sealed class SlidingWindowSender(
    ProtocolReader reader,
    ProtocolWriter writer,
    int chunkSize = ProtocolConstants.DefaultChunkSize,
    int windowChunks = ProtocolConstants.DefaultWindowChunks)
{
    private readonly SemaphoreSlim _window = new(windowChunks, windowChunks);
    private readonly ConcurrentDictionary<long, InFlightChunk> _inFlight = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _safeOffset;
    private long _receivedOffset;
    private int _sendingFinished;
    private long _lastInboundTicks;

    public event Action<SenderProgress>? Progress;
    public event Action<ChunkRange>? ChunkSent;
    public event Action<ChunkRange>? ChunkAcknowledged;
    public event Action<CheckpointMessage>? CheckpointReceived;

    public async ValueTask<FileCompleteMessage> SendFileAsync(
        FileInfo source,
        FileStartMessage file,
        ResumeInfoMessage resume,
        SourceFingerprint expectedSource,
        CancellationToken cancellationToken = default)
    {
        if (file.FileId != resume.FileId || file.TransferId != resume.TransferId)
            throw new ProtocolException("Resume response does not match the file start request.");
        if (resume.CommittedOffset < 0 || resume.CommittedOffset > file.Size ||
            (resume.CommittedOffset != file.Size && resume.CommittedOffset % chunkSize != 0))
            throw new ProtocolException("Receiver returned an invalid committed offset.");
        if (!expectedSource.Matches(source))
            throw new SourceChangedException(source.FullName);

        var snapshot = string.IsNullOrEmpty(resume.MerkleSnapshotBase64)
            ? []
            : Convert.FromBase64String(resume.MerkleSnapshotBase64);
        var merkle = MerkleAccumulator.ImportLeaves(snapshot);
        var expectedLeaves = resume.CommittedOffset == 0
            ? 0
            : checked((int)((resume.CommittedOffset + chunkSize - 1) / chunkSize));
        if (merkle.LeafCount != resume.CommittedLeaves || merkle.LeafCount != expectedLeaves)
            throw new ProtocolException("Receiver Merkle state is inconsistent with its committed offset.");

        Interlocked.Exchange(ref _safeOffset, resume.CommittedOffset);
        Interlocked.Exchange(ref _receivedOffset, resume.CommittedOffset);
        using var ackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _lastInboundTicks, Stopwatch.GetTimestamp());
        var ackLoop = RunAckLoopAsync(file.FileId, ackCancellation.Token);
        var heartbeatLoop = RunHeartbeatAsync(writer, ackCancellation.Token);

        try
        {
            await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                chunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = resume.CommittedOffset;
            var offset = resume.CommittedOffset;
            while (offset < file.Size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!expectedSource.Matches(source))
                    throw new SourceChangedException(source.FullName);

                await _window.WaitAsync(cancellationToken).ConfigureAwait(false);
                var owner = MemoryPool<byte>.Shared.Rent(chunkSize);
                var length = 0;
                try
                {
                    var wanted = checked((int)Math.Min(chunkSize, file.Size - offset));
                    while (length < wanted)
                    {
                        var read = await input.ReadAsync(owner.Memory.Slice(length, wanted - length), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            throw new EndOfStreamException("Source file ended before its declared size.");
                        length += read;
                    }
                    merkle.AddChunk(owner.Memory.Span[..length]);
                    var pending = new InFlightChunk(owner, length);
                    if (!_inFlight.TryAdd(offset, pending))
                        throw new InvalidOperationException("Duplicate in-flight chunk offset.");
                    await writer.WriteChunkAsync(file.FileId, offset, owner.Memory[..length], cancellationToken: cancellationToken).ConfigureAwait(false);
                    ChunkSent?.Invoke(new ChunkRange(file.FileId, offset, length));
                    offset = checked(offset + length);
                    owner = null!;
                }
                finally
                {
                    if (owner is not null)
                    {
                        owner.Dispose();
                        _window.Release();
                    }
                }
            }

            Interlocked.Exchange(ref _sendingFinished, 1);
            if (_inFlight.IsEmpty)
                _drained.TrySetResult();
            var first = await Task.WhenAny(_drained.Task, heartbeatLoop).ConfigureAwait(false);
            if (first == heartbeatLoop)
                await heartbeatLoop.ConfigureAwait(false);
            await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!expectedSource.Matches(source))
                throw new SourceChangedException(source.FullName);

            var complete = new FileCompleteMessage(file.FileId, file.Size, merkle.LeafCount, Convert.ToBase64String(merkle.ComputeRoot()));
            await writer.WriteControlAsync(MessageType.FileComplete, complete, FrameFlags.Final, cancellationToken).ConfigureAwait(false);
            return complete;
        }
        finally
        {
            await ackCancellation.CancelAsync().ConfigureAwait(false);
            foreach (var entry in _inFlight)
                if (_inFlight.TryRemove(entry.Key, out var pending)) pending.Owner.Dispose();
            Interlocked.Exchange(ref _sendingFinished, 1);
            try { await ackLoop.ConfigureAwait(false); } catch (OperationCanceledException) when (ackCancellation.IsCancellationRequested) { }
            try { await heartbeatLoop.ConfigureAwait(false); } catch (OperationCanceledException) when (ackCancellation.IsCancellationRequested) { }
        }
    }

    private async Task RunAckLoopAsync(Guid fileId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var frame = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastInboundTicks, Stopwatch.GetTimestamp());
            switch (frame.Header.Type)
            {
                case MessageType.ChunkAck:
                {
                    var ack = ControlFrameCodec.Deserialize<ChunkAckMessage>(frame.Payload.Span);
                    if (ack.FileId != fileId)
                        throw new ProtocolException("ACK belongs to a different file.");
                    if (_inFlight.TryRemove(ack.Offset, out var pending))
                    {
                        pending.Owner.Dispose();
                        _window.Release();
                    }
                    Interlocked.Exchange(ref _receivedOffset, Math.Max(Interlocked.Read(ref _receivedOffset), ack.ReceivedOffset));
                    ChunkAcknowledged?.Invoke(new ChunkRange(ack.FileId, ack.Offset, ack.Length));
                    RaiseProgress();
                    if (Volatile.Read(ref _sendingFinished) == 1 && _inFlight.IsEmpty)
                    {
                        _drained.TrySetResult();
                        return;
                    }
                    break;
                }
                case MessageType.Checkpoint:
                {
                    var checkpoint = ControlFrameCodec.Deserialize<CheckpointMessage>(frame.Payload.Span);
                    if (checkpoint.FileId != fileId || checkpoint.CommittedOffset < Interlocked.Read(ref _safeOffset))
                        throw new ProtocolException("Invalid checkpoint progression.");
                    Interlocked.Exchange(ref _safeOffset, checkpoint.CommittedOffset);
                    CheckpointReceived?.Invoke(checkpoint);
                    RaiseProgress();
                    break;
                }
                case MessageType.Ping:
                    await writer.WriteControlAsync(MessageType.Pong, ControlFrameCodec.Deserialize<PingMessage>(frame.Payload.Span), FrameFlags.Response, cancellationToken).ConfigureAwait(false);
                    break;
                case MessageType.Pong:
                    break;
                case MessageType.Error:
                    var error = ControlFrameCodec.Deserialize<ErrorMessage>(frame.Payload.Span);
                    if (error.Code == "CHUNK_RETRY" && error.FileId == fileId && error.Offset is { } offset &&
                        _inFlight.TryGetValue(offset, out var failedChunk))
                    {
                        if (Interlocked.Increment(ref failedChunk.RetryCount) >= 3)
                            throw new RemoteTransferException(error with { Code = "CONNECTION_REBUILD" });
                        await writer.WriteChunkAsync(fileId, offset, failedChunk.Owner.Memory[..failedChunk.Length],
                            FrameFlags.Retry, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    throw new RemoteTransferException(error);
                default:
                    throw new ProtocolException($"Unexpected {frame.Header.Type} while waiting for acknowledgements.");
            }
        }
    }

    private void RaiseProgress() => Progress?.Invoke(new SenderProgress(
        Interlocked.Read(ref _safeOffset),
        Interlocked.Read(ref _receivedOffset),
        _inFlight.Count));

    private async Task RunHeartbeatAsync(ProtocolWriter writer, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            var silence = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastInboundTicks));
            if (silence > TimeSpan.FromSeconds(45))
                throw new TimeoutException("Peer stopped responding to the transfer watchdog.");
            await writer.WriteControlAsync(MessageType.Ping, new PingMessage(Stopwatch.GetTimestamp()),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class InFlightChunk(IMemoryOwner<byte> owner, int length)
    {
        public IMemoryOwner<byte> Owner { get; } = owner;
        public int Length { get; } = length;
        public int RetryCount;
    }
}

public sealed record SenderProgress(long SafeOffset, long ReceiverWrittenOffset, int InFlightChunks);
public sealed record ChunkRange(Guid FileId, long Offset, int Length);

public sealed class SourceChangedException(string path)
    : IOException($"Source file changed during transfer: {path}");

public sealed class RemoteTransferException(ErrorMessage error)
    : IOException($"Remote transfer error {error.Code}: {error.UserMessage}")
{
    public ErrorMessage Error { get; } = error;
}
