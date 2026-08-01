using System.Security.Cryptography;
using Eslee.QuickSend.Core.Integrity;
using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Protocol;

namespace Eslee.QuickSend.Core.Transfers;

public static class CompletedFileReplay
{
    public static bool CanReplay(TransferFileRecord record, bool completedFileExists) =>
        completedFileExists &&
        record.State == TransferState.Completed &&
        record.CommittedOffset == record.Size &&
        record.FinalPath is not null;

    public static ResumeInfoMessage CreateResume(TransferFileRecord record)
    {
        if (record.State != TransferState.Completed || record.CommittedOffset != record.Size)
            throw new ProtocolException("Only a durably completed file can be replayed.");
        var merkle = MerkleAccumulator.ImportLeaves(record.MerkleLeaves);
        var expectedLeaves = record.Size == 0
            ? 0
            : checked((int)((record.Size + record.ChunkSize - 1) / record.ChunkSize));
        if (merkle.LeafCount != expectedLeaves)
            throw new ProtocolException("Completed file Merkle state is inconsistent with its size.");
        return new ResumeInfoMessage(
            record.TransferId,
            record.FileId,
            record.Size,
            merkle.LeafCount,
            Convert.ToBase64String(record.MerkleLeaves));
    }

    public static void VerifyCompletion(TransferFileRecord record, FileCompleteMessage complete)
    {
        if (complete.FileId != record.FileId || complete.Size != record.Size)
            throw new ProtocolException("Completed replay metadata does not match the stored file.");
        var merkle = MerkleAccumulator.ImportLeaves(record.MerkleLeaves);
        var expectedRoot = merkle.ComputeRoot();
        var actualRoot = Convert.FromBase64String(complete.MerkleRootBase64);
        if (complete.LeafCount != merkle.LeafCount ||
            !CryptographicOperations.FixedTimeEquals(expectedRoot, actualRoot))
            throw new FileIntegrityException(record.FileId);
    }
}
