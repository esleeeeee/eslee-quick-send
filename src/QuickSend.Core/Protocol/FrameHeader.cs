using System.Buffers.Binary;

namespace Eslee.QuickSend.Core.Protocol;

public readonly record struct FrameHeader(
    MessageType Type,
    FrameFlags Flags,
    ulong Sequence,
    ulong PayloadLength,
    ushort Version = ProtocolConstants.Version)
{
    public void Write(Span<byte> destination)
    {
        if (destination.Length < ProtocolConstants.HeaderSize)
            throw new ArgumentException("Frame header buffer is too small.", nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination, ProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], ProtocolConstants.HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], Version);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], (ushort)Type);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], (ushort)Flags);
        BinaryPrimitives.WriteUInt64BigEndian(destination[12..], Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(destination[20..], PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[28..], 0);
    }

    public static FrameHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < ProtocolConstants.HeaderSize)
            throw new ProtocolException("Truncated frame header.");
        if (BinaryPrimitives.ReadUInt32BigEndian(source) != ProtocolConstants.Magic)
            throw new ProtocolException("Invalid frame magic.");
        if (BinaryPrimitives.ReadUInt16BigEndian(source[4..]) != ProtocolConstants.HeaderSize)
            throw new ProtocolException("Unsupported frame header size.");

        var version = BinaryPrimitives.ReadUInt16BigEndian(source[6..]);
        if (version != ProtocolConstants.Version)
            throw new ProtocolException($"Unsupported protocol version {version}.");

        var rawType = BinaryPrimitives.ReadUInt16BigEndian(source[8..]);
        if (!Enum.IsDefined((MessageType)rawType))
            throw new ProtocolException($"Unknown message type {rawType}.");
        if (BinaryPrimitives.ReadUInt32BigEndian(source[28..]) != 0)
            throw new ProtocolException("Reserved header field must be zero.");

        return new FrameHeader(
            (MessageType)rawType,
            (FrameFlags)BinaryPrimitives.ReadUInt16BigEndian(source[10..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[12..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[20..]),
            version);
    }
}

