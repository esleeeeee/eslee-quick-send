using System.Buffers;
using System.Buffers.Binary;

namespace Eslee.QuickSend.Core.Protocol;

public sealed class ProtocolReader(Stream stream)
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private ulong _lastSequence;

    public async ValueTask<InboundFrame> ReadAsync(CancellationToken cancellationToken = default)
    {
        var rawHeader = new byte[ProtocolConstants.HeaderSize];
        await _stream.ReadExactlyAsync(rawHeader, cancellationToken).ConfigureAwait(false);
        var header = FrameHeader.Parse(rawHeader);
        if (header.Sequence <= _lastSequence)
            throw new ProtocolException("Frame sequence is duplicated or out of order.");
        _lastSequence = header.Sequence;

        var limit = header.Type == MessageType.ChunkData
            ? ProtocolConstants.MaxChunkData + (ulong)ProtocolConstants.ChunkMetadataSize
            : ProtocolConstants.MaxControlPayload;
        if (header.PayloadLength > limit)
            throw new ProtocolException($"Payload length {header.PayloadLength} exceeds its limit.");
        if (header.PayloadLength > int.MaxValue)
            throw new ProtocolException("Payload cannot be represented by this runtime.");

        var owner = MemoryPool<byte>.Shared.Rent((int)Math.Max(header.PayloadLength, 1));
        try
        {
            var payload = owner.Memory[..(int)header.PayloadLength];
            await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            if (header.Type == MessageType.ChunkData)
                ValidateChunkPayload(payload.Span);
            return new InboundFrame(header, owner, payload.Length);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private static void ValidateChunkPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ProtocolConstants.ChunkMetadataSize)
            throw new ProtocolException("Truncated chunk payload.");
        var dataLength = BinaryPrimitives.ReadUInt32BigEndian(payload[24..]);
        if (dataLength > ProtocolConstants.MaxChunkData || payload.Length != ProtocolConstants.ChunkMetadataSize + dataLength)
            throw new ProtocolException("Chunk payload length is inconsistent.");
    }
}

public sealed class InboundFrame : IDisposable
{
    private IMemoryOwner<byte>? _owner;

    internal InboundFrame(FrameHeader header, IMemoryOwner<byte> owner, int length)
    {
        Header = header;
        _owner = owner;
        Payload = owner.Memory[..length];
    }

    public FrameHeader Header { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}

