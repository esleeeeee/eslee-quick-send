using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace Eslee.QuickSend.Windows.Persistence;

/// <summary>
/// Reads and prunes transfer history rows.
/// </summary>
/// <remarks>
/// Every operation here touches database bookkeeping only. Sent source files and
/// received files are never deleted. A row is protected while a worker still owns its
/// job; <c>runningTransferIds</c> is that ownership set.
/// </remarks>
public sealed class HistoryService(AppDatabase database)
{
    /// <summary>Raised after history rows are removed, so the list can re-query itself.</summary>
    public event EventHandler? Changed;

    public async IAsyncEnumerable<HistoryListItem> GetRecentAsync(
        IReadOnlySet<Guid>? runningTransferIds = null,
        HistoryPresentation? presentation = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.file_id,f.relative_path,f.size,f.final_path,j.direction,j.state,j.updated_utc,
                   j.transfer_id,j.error_code,j.destination_device_id
            FROM transfer_files f JOIN transfer_jobs j ON j.transfer_id=f.transfer_id
            WHERE j.updated_utc >= $cutoff ORDER BY j.updated_utc DESC LIMIT 100;
            """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var direction = (TransferDirection)reader.GetInt32(4);
            var state = (TransferState)reader.GetInt32(5);
            var transferId = Guid.Parse(reader.GetString(7));
            var errorCode = reader.IsDBNull(8) ? null : reader.GetString(8);
            var destinationDeviceId = reader.GetString(9);
            var location = direction == TransferDirection.Receive && !reader.IsDBNull(3)
                ? reader.GetString(3)
                : reader.GetString(1);
            // Only an outgoing job can be waiting behind another outgoing job.
            var queuePosition = direction == TransferDirection.Send
                ? presentation?.QueuePosition(transferId)
                : null;
            yield return new HistoryListItem(
                Guid.Parse(reader.GetString(0)),
                Path.GetFileName(reader.GetString(1)),
                $"{DirectionText(direction)} · {FormatSize(reader.GetInt64(2))} · {DateTimeOffset.Parse(reader.GetString(6)).LocalDateTime:g}",
                TransferStatusText.ForJob(
                    state,
                    errorCode,
                    queuePosition,
                    presentation?.IsDestinationOnline(destinationDeviceId) ?? true),
                IsDeletable(transferId, state, runningTransferIds),
                location);
        }
    }

    public async Task<bool> DeleteFileAsync(
        Guid fileId,
        IReadOnlySet<Guid>? runningTransferIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var owner = await FindOwningJobAsync(connection, (SqliteTransaction)transaction, fileId, cancellationToken);
        if (owner is null || !IsDeletable(owner.Value.TransferId, owner.Value.State, runningTransferIds))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var deleteFile = connection.CreateCommand();
        deleteFile.Transaction = (SqliteTransaction)transaction;
        deleteFile.CommandText = "DELETE FROM transfer_files WHERE file_id=$file;";
        deleteFile.Parameters.AddWithValue("$file", fileId.ToString("D"));
        var deleted = await deleteFile.ExecuteNonQueryAsync(cancellationToken) == 1;

        var deleteEmptyJob = connection.CreateCommand();
        deleteEmptyJob.Transaction = (SqliteTransaction)transaction;
        deleteEmptyJob.CommandText = """
            DELETE FROM transfer_jobs
            WHERE transfer_id=$transfer
              AND NOT EXISTS(SELECT 1 FROM transfer_files WHERE transfer_id=$transfer);
            """;
        deleteEmptyJob.Parameters.AddWithValue("$transfer", owner.Value.TransferId.ToString("D"));
        await deleteEmptyJob.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return deleted;
    }

    /// <summary>
    /// Removes finished history plus stale rows that never reached a terminal state.
    /// Jobs a worker still owns are kept.
    /// </summary>
    public async Task<HistoryClearResult> ClearAsync(
        IReadOnlySet<Guid>? runningTransferIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var scan = connection.CreateCommand();
        scan.Transaction = (SqliteTransaction)transaction;
        scan.CommandText = "SELECT transfer_id,state FROM transfer_jobs;";
        var removable = new List<string>();
        var kept = 0;
        await using (var reader = await scan.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var transferId = Guid.Parse(reader.GetString(0));
                var state = (TransferState)reader.GetInt32(1);
                if (IsDeletable(transferId, state, runningTransferIds)) removable.Add(transferId.ToString("D"));
                else kept++;
            }
        }

        var deletedFiles = 0;
        foreach (var transferId in removable)
        {
            var deleteFiles = connection.CreateCommand();
            deleteFiles.Transaction = (SqliteTransaction)transaction;
            deleteFiles.CommandText = "DELETE FROM transfer_files WHERE transfer_id=$transfer;";
            deleteFiles.Parameters.AddWithValue("$transfer", transferId);
            deletedFiles += await deleteFiles.ExecuteNonQueryAsync(cancellationToken);

            var deleteJob = connection.CreateCommand();
            deleteJob.Transaction = (SqliteTransaction)transaction;
            deleteJob.CommandText = "DELETE FROM transfer_jobs WHERE transfer_id=$transfer;";
            deleteJob.Parameters.AddWithValue("$transfer", transferId);
            await deleteJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
        return new HistoryClearResult(deletedFiles, removable.Count, kept);
    }

    private static bool IsDeletable(Guid transferId, TransferState state, IReadOnlySet<Guid>? runningTransferIds)
    {
        // Without a live ownership set we take the conservative reading: anything that is
        // not settled is assumed to still be running.
        var isRunning = runningTransferIds?.Contains(transferId) ?? !TransferHistoryPolicy.IsSettled(state);
        return TransferHistoryPolicy.CanDeleteHistory(state, isRunning);
    }

    private static async Task<(Guid TransferId, TransferState State)?> FindOwningJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT f.transfer_id,j.state
            FROM transfer_files f JOIN transfer_jobs j ON j.transfer_id=f.transfer_id
            WHERE f.file_id=$file;
            """;
        command.Parameters.AddWithValue("$file", fileId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (Guid.Parse(reader.GetString(0)), (TransferState)reader.GetInt32(1));
    }

    private static string DirectionText(TransferDirection direction) =>
        direction == TransferDirection.Send ? "보냄" : "받음";

    private static string FormatSize(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)value;
        var index = 0;
        while (amount >= 1024 && index < units.Length - 1) { amount /= 1024; index++; }
        return $"{amount:0.#} {units[index]}";
    }
}

/// <summary>
/// Live coordinator facts the history list needs to word a row correctly. Kept as a
/// callback pair so the persistence layer does not depend on the transfer engine.
/// </summary>
public sealed record HistoryPresentation(
    Func<Guid, int?> QueuePosition,
    Func<string, bool> IsDestinationOnline);

public sealed record HistoryClearResult(int DeletedFiles, int DeletedJobs, int KeptRunningJobs);

public sealed record HistoryListItem(
    Guid FileId,
    string Title,
    string Subtitle,
    string StateText,
    bool CanDelete,
    string Location);
