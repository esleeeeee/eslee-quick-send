using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Eslee.QuickSend.Core.Protocol;

public sealed class ProtocolWriter(Stream stream) : IAsyncDisposable
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _sequence;

    public async ValueTask WriteControlAsync<T>(MessageType type, T message, FrameFlags flags = FrameFlags.None, CancellationToken cancellationToken = default)
        where T : notnull
    {
        if (type == MessageType.ChunkData)
            throw new ArgumentException("Use WriteChunkAsync for chunk data.", nameof(type));
        var payload = ControlFrameCodec.Serialize(message);
        if ((ulong)payload.Length > ProtocolConstants.MaxControlPayload)
            throw new ProtocolException("Control payload exceeds the protocol limit.");
        await WriteFrameAsync(type, flags, payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteChunkAsync(Guid fileId, long offset, ReadOnlyMemory<byte> data, FrameFlags flags = FrameFlags.None, CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if ((ulong)data.Length > ProtocolConstants.MaxChunkData)
            throw new ArgumentOutOfRangeException(nameof(data));

        var metadata = new byte[ProtocolConstants.ChunkMetadataSize];
        fileId.TryWriteBytes(metadata, bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(metadata.AsSpan(16), offset);
        BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(24), (uint)data.Length);
        SHA256.HashData(data.Span, metadata.AsSpan(28, 32));

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteHeaderUnlockedAsync(MessageType.ChunkData, flags, (ulong)(metadata.Length + data.Length), cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask WriteFrameAsync(MessageType type, FrameFlags flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteHeaderUnlockedAsync(type, flags, (ulong)payload.Length, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ValueTask WriteHeaderUnlockedAsync(MessageType type, FrameFlags flags, ulong payloadLength, CancellationToken cancellationToken)
    {
        var raw = new byte[ProtocolConstants.HeaderSize];
        var sequence = checked((ulong)Interlocked.Increment(ref _sequence));
        new FrameHeader(type, flags, sequence, payloadLength).Write(raw);
        return _stream.WriteAsync(raw, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

