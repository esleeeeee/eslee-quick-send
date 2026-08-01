using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Eslee.QuickSend.Core.Protocol;

public readonly ref struct ChunkPayload
{
    private readonly ReadOnlySpan<byte> _payload;

    public ChunkPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ProtocolConstants.ChunkMetadataSize)
            throw new ProtocolException("Truncated chunk payload.");
        var declared = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[24..]));
        if (declared != payload.Length - ProtocolConstants.ChunkMetadataSize)
            throw new ProtocolException("Invalid chunk data length.");
        _payload = payload;
    }

    public Guid FileId => new(_payload[..16], bigEndian: true);
    public long Offset => BinaryPrimitives.ReadInt64BigEndian(_payload[16..]);
    public int Length => checked((int)BinaryPrimitives.ReadUInt32BigEndian(_payload[24..]));
    public ReadOnlySpan<byte> ExpectedHash => _payload.Slice(28, 32);
    public ReadOnlySpan<byte> Data => _payload[ProtocolConstants.ChunkMetadataSize..];

    public bool VerifyHash()
    {
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(Data, actual);
        return CryptographicOperations.FixedTimeEquals(actual, ExpectedHash);
    }
}

