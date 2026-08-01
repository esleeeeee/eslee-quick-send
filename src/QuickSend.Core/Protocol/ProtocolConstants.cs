namespace Eslee.QuickSend.Core.Protocol;

public static class ProtocolConstants
{
    public const uint Magic = 0x45535131; // ESQ1
    public const ushort HeaderSize = 32;
    public const ushort Version = 1;
    public const int DefaultChunkSize = 8 * 1024 * 1024;
    public const int DefaultWindowChunks = 8;
    public const long CheckpointBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(1);
    public const ulong MaxControlPayload = 4UL * 1024 * 1024;
    public const ulong MaxChunkData = 64UL * 1024 * 1024;
    public const int ChunkMetadataSize = 16 + 8 + 4 + 32;
    public const string ServiceType = "_eslee-quicksend._tcp";
    public const int DefaultPort = 41231;
}

public enum MessageType : ushort
{
    Hello = 1,
    PairRequest = 2,
    PairAccept = 3,
    JobManifest = 4,
    FileStart = 5,
    ResumeInfo = 6,
    ChunkData = 7,
    ChunkAck = 8,
    Checkpoint = 9,
    FileComplete = 10,
    FileVerify = 11,
    JobComplete = 12,
    Pause = 13,
    Resume = 14,
    Cancel = 15,
    Ping = 16,
    Pong = 17,
    Error = 18
}

[Flags]
public enum FrameFlags : ushort
{
    None = 0,
    Response = 1,
    Final = 2,
    Retry = 4
}

