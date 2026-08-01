using Microsoft.Data.Sqlite;

namespace Eslee.QuickSend.Windows.Persistence;

public sealed class TrustedDeviceStore(AppDatabase database)
{
    public async ValueTask TrustAsync(string deviceId, string displayName, string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trusted_devices(device_id,display_name,fingerprint,paired_utc,last_seen_utc,revoked_utc)
            VALUES($id,$name,$fingerprint,$now,$now,NULL)
            ON CONFLICT(device_id) DO UPDATE SET display_name=excluded.display_name,fingerprint=excluded.fingerprint,last_seen_utc=excluded.last_seen_utc,revoked_utc=NULL;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<bool> IsTrustedAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM trusted_devices WHERE fingerprint=$fingerprint AND revoked_utc IS NULL);";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async ValueTask<HashSet<string>> GetFingerprintsAsync(CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint FROM trusted_devices WHERE revoked_utc IS NULL;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async ValueTask RevokeAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE trusted_devices SET revoked_utc=$now WHERE device_id=$id;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

