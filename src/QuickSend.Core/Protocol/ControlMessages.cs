using System.Text.Json.Serialization;

namespace Eslee.QuickSend.Core.Protocol;

public sealed record HelloMessage(
    int ProtocolVersion,
    string AppVersion,
    string DeviceId,
    string DeviceName,
    string Platform,
    string IdentityFingerprint,
    IReadOnlyList<string> Capabilities);

public sealed record PairRequestMessage(string DeviceId, string DeviceName, string Nonce, string IdentityFingerprint);

public sealed record PairAcceptMessage(string DeviceId, string Nonce, bool Accepted);

public sealed record ManifestMessage(Guid TransferId, string SourceDeviceId, string DestinationDeviceId, IReadOnlyList<ManifestFile> Files);

public sealed record ManifestFile(
    Guid FileId,
    string RelativePath,
    long Size,
    long ModifiedUtcTicks,
    int ChunkSize,
    string? StableSourceId);

public sealed record FileStartMessage(
    Guid TransferId,
    Guid FileId,
    string RelativePath,
    long Size,
    long ModifiedUtcTicks,
    int ChunkSize,
    string? StableSourceId);

public sealed record ResumeInfoMessage(Guid TransferId, Guid FileId, long CommittedOffset, int CommittedLeaves, string? MerkleSnapshotBase64);

public sealed record ChunkAckMessage(Guid FileId, long Offset, int Length, long ReceivedOffset);

public sealed record CheckpointMessage(Guid FileId, long CommittedOffset, int CommittedLeaves, string MerkleSnapshotBase64);

public sealed record FileCompleteMessage(Guid FileId, long Size, int LeafCount, string MerkleRootBase64);

public sealed record FileVerifyMessage(Guid FileId, bool Verified, string? ErrorCode = null);

public sealed record TransferControlMessage(Guid TransferId, Guid? FileId = null);

public sealed record PingMessage(long MonotonicTicks);

[JsonConverter(typeof(JsonStringEnumConverter<RecoveryClass>))]
public enum RecoveryClass
{
    Recoverable,
    WaitForCondition,
    UserActionRequired,
    Fatal
}

public sealed record ErrorMessage(
    string Code,
    string UserMessage,
    RecoveryClass RecoveryClass,
    Guid? FileId = null,
    long? Offset = null);
