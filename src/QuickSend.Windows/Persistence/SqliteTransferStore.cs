using Eslee.QuickSend.Core.Persistence;
using Eslee.QuickSend.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace Eslee.QuickSend.Windows.Persistence;

public sealed class SqliteTransferStore(AppDatabase database) : ITransferStore
{
    /// <summary>
    /// Raised after a write that can change what the history list shows. It fires once the
    /// row is committed, so a listener that re-queries always observes the new data.
    /// Checkpoints are deliberately excluded: they fire per chunk and never alter history.
    /// </summary>
    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask UpsertJobAsync(TransferJobRecord job, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfer_jobs(transfer_id, source_device_id, destination_device_id, direction, state, created_utc, updated_utc, completed_utc, error_code)
            VALUES($id,$source,$destination,$direction,$state,$created,$updated,$completed,$error)
            ON CONFLICT(transfer_id) DO UPDATE SET
              source_device_id=excluded.source_device_id, destination_device_id=excluded.destination_device_id,
              direction=excluded.direction, state=excluded.state, updated_utc=excluded.updated_utc,
              completed_utc=excluded.completed_utc, error_code=excluded.error_code;
            """;
        command.Parameters.AddWithValue("$id", job.TransferId.ToString("D"));
        command.Parameters.AddWithValue("$source", job.SourceDeviceId);
        command.Parameters.AddWithValue("$destination", job.DestinationDeviceId);
        command.Parameters.AddWithValue("$direction", (int)job.Direction);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed", (object?)job.CompletedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)job.ErrorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        RaiseChanged();
    }

    public async ValueTask UpsertFileAsync(TransferFileRecord file, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfer_files(file_id,transfer_id,relative_path,source_location,partial_path,final_path,size,modified_ticks,stable_source_id,chunk_size,received_offset,committed_offset,merkle_leaves,state,retry_count,error_code,updated_utc)
            VALUES($file,$transfer,$relative,$source,$partial,$final,$size,$modified,$stable,$chunk,$received,$committed,$leaves,$state,$retry,$error,$updated)
            ON CONFLICT(file_id) DO UPDATE SET relative_path=excluded.relative_path, source_location=excluded.source_location,
              partial_path=excluded.partial_path, final_path=excluded.final_path, size=excluded.size, modified_ticks=excluded.modified_ticks,
              stable_source_id=excluded.stable_source_id, chunk_size=excluded.chunk_size, received_offset=excluded.received_offset,
              committed_offset=excluded.committed_offset, merkle_leaves=excluded.merkle_leaves, state=excluded.state,
              retry_count=excluded.retry_count, error_code=excluded.error_code, updated_utc=excluded.updated_utc;
            """;
        AddFileParameters(command, file);
        await command.ExecuteNonQueryAsync(cancellationToken);
        RaiseChanged();
    }

    public async ValueTask SaveCheckpointAsync(Guid fileId, long committedOffset, byte[] merkleLeaves, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE transfer_files SET received_offset=$offset, committed_offset=$offset, merkle_leaves=$leaves,
              state=$state, updated_utc=$at WHERE file_id=$id AND committed_offset <= $offset;
            """;
        command.Parameters.AddWithValue("$offset", committedOffset);
        command.Parameters.Add("$leaves", SqliteType.Blob).Value = merkleLeaves;
        command.Parameters.AddWithValue("$state", (int)TransferState.Transferring);
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.Parameters.AddWithValue("$id", fileId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Checkpoint target {fileId} does not exist or moved backwards.");
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask MarkFileCompletedAsync(Guid fileId, string finalPath, byte[] merkleRoot, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE transfer_files SET final_path=$path, partial_path=NULL, merkle_root=$root, state=$state, updated_utc=$at WHERE file_id=$id;";
        command.Parameters.AddWithValue("$path", finalPath);
        command.Parameters.Add("$root", SqliteType.Blob).Value = merkleRoot;
        command.Parameters.AddWithValue("$state", (int)TransferState.Completed);
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.Parameters.AddWithValue("$id", fileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        RaiseChanged();
    }

    public async ValueTask<TransferFileRecord?> FindFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM transfer_files WHERE file_id=$id;";
        command.Parameters.AddWithValue("$id", fileId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    public async IAsyncEnumerable<TransferJobRecord> FindRecoverableJobsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM transfer_jobs WHERE state NOT IN ($completed,$cancelled,$fatal) ORDER BY updated_utc;";
        command.Parameters.AddWithValue("$completed", (int)TransferState.Completed);
        command.Parameters.AddWithValue("$cancelled", (int)TransferState.Cancelled);
        command.Parameters.AddWithValue("$fatal", (int)TransferState.FailedFatal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) yield return ReadJob(reader);
    }

    public async ValueTask<IReadOnlyList<TransferFileRecord>> FindFilesAsync(Guid transferId, CancellationToken cancellationToken)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM transfer_files WHERE transfer_id=$id ORDER BY rowid;";
        command.Parameters.AddWithValue("$id", transferId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var files = new List<TransferFileRecord>();
        while (await reader.ReadAsync(cancellationToken)) files.Add(ReadFile(reader));
        return files;
    }

    private static void AddFileParameters(SqliteCommand command, TransferFileRecord file)
    {
        command.Parameters.AddWithValue("$file", file.FileId.ToString("D"));
        command.Parameters.AddWithValue("$transfer", file.TransferId.ToString("D"));
        command.Parameters.AddWithValue("$relative", file.RelativePath);
        command.Parameters.AddWithValue("$source", file.SourceLocation);
        command.Parameters.AddWithValue("$partial", (object?)file.PartialPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$final", (object?)file.FinalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$modified", file.ModifiedUtcTicks);
        command.Parameters.AddWithValue("$stable", (object?)file.StableSourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$chunk", file.ChunkSize);
        command.Parameters.AddWithValue("$received", file.ReceivedOffset);
        command.Parameters.AddWithValue("$committed", file.CommittedOffset);
        command.Parameters.Add("$leaves", SqliteType.Blob).Value = file.MerkleLeaves;
        command.Parameters.AddWithValue("$state", (int)file.State);
        command.Parameters.AddWithValue("$retry", file.RetryCount);
        command.Parameters.AddWithValue("$error", (object?)file.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
    }

    private static TransferFileRecord ReadFile(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("transfer_id"))),
        Guid.Parse(reader.GetString(reader.GetOrdinal("file_id"))),
        reader.GetString(reader.GetOrdinal("relative_path")), reader.GetString(reader.GetOrdinal("source_location")),
        GetNullableString(reader, "partial_path"), GetNullableString(reader, "final_path"),
        reader.GetInt64(reader.GetOrdinal("size")), reader.GetInt64(reader.GetOrdinal("modified_ticks")),
        GetNullableString(reader, "stable_source_id"), reader.GetInt32(reader.GetOrdinal("chunk_size")),
        reader.GetInt64(reader.GetOrdinal("received_offset")), reader.GetInt64(reader.GetOrdinal("committed_offset")),
        (byte[])reader["merkle_leaves"], (TransferState)reader.GetInt32(reader.GetOrdinal("state")),
        reader.GetInt32(reader.GetOrdinal("retry_count")), GetNullableString(reader, "error_code"));

    private static TransferJobRecord ReadJob(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(reader.GetOrdinal("transfer_id"))),
        reader.GetString(reader.GetOrdinal("source_device_id")), reader.GetString(reader.GetOrdinal("destination_device_id")),
        (TransferDirection)reader.GetInt32(reader.GetOrdinal("direction")), (TransferState)reader.GetInt32(reader.GetOrdinal("state")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_utc"))), DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_utc"))),
        GetNullableDate(reader, "completed_utc"), GetNullableString(reader, "error_code"));

    private static string? GetNullableString(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
    private static DateTimeOffset? GetNullableDate(SqliteDataReader reader, string name) => GetNullableString(reader, name) is { } value ? DateTimeOffset.Parse(value) : null;
}
