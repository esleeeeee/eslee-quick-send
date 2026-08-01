using Microsoft.Data.Sqlite;

namespace Eslee.QuickSend.Windows.Persistence;

public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException("Database path requires a parent directory.", nameof(path)));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public SqliteConnection OpenConnection() => new(_connectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous=FULL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken);
        await ExecuteAsync(connection, SchemaV1, cancellationToken);
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO schema_info(version) VALUES (1);", cancellationToken);
    }

    public async ValueTask<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async ValueTask SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS settings(
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS trusted_devices(
            device_id TEXT PRIMARY KEY NOT NULL,
            display_name TEXT NOT NULL,
            fingerprint TEXT NOT NULL UNIQUE,
            paired_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            revoked_utc TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS transfer_jobs(
            transfer_id TEXT PRIMARY KEY NOT NULL,
            source_device_id TEXT NOT NULL,
            destination_device_id TEXT NOT NULL,
            direction INTEGER NOT NULL,
            state INTEGER NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            completed_utc TEXT NULL,
            error_code TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS transfer_files(
            file_id TEXT PRIMARY KEY NOT NULL,
            transfer_id TEXT NOT NULL REFERENCES transfer_jobs(transfer_id) ON DELETE CASCADE,
            relative_path TEXT NOT NULL,
            source_location TEXT NOT NULL,
            partial_path TEXT NULL,
            final_path TEXT NULL,
            size INTEGER NOT NULL CHECK(size >= 0),
            modified_ticks INTEGER NOT NULL,
            stable_source_id TEXT NULL,
            chunk_size INTEGER NOT NULL,
            received_offset INTEGER NOT NULL DEFAULT 0,
            committed_offset INTEGER NOT NULL DEFAULT 0,
            merkle_leaves BLOB NOT NULL,
            merkle_root BLOB NULL,
            state INTEGER NOT NULL,
            retry_count INTEGER NOT NULL DEFAULT 0,
            error_code TEXT NULL,
            updated_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_jobs_state ON transfer_jobs(state, updated_utc);
        CREATE INDEX IF NOT EXISTS ix_files_transfer ON transfer_files(transfer_id);
        """;
}
