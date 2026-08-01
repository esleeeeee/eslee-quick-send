using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eslee.QuickSend.Windows.Diagnostics;

public sealed class DiagnosticLog
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DiagnosticLog(string directory) => _directory = directory;

    public void Info(string eventName, object? data = null) => _ = InfoAsync(eventName, data);
    public void Warn(string eventName, object? data = null) => _ = WarnAsync(eventName, data);
    public void Error(string eventName, Exception exception) => _ = ErrorAsync(eventName, exception);
    public void Error(string eventName, Exception exception, object? data) => _ = ErrorAsync(eventName, exception, data);
    public Task InfoAsync(string eventName, object? data = null) => WriteAsync("info", eventName, data, null);
    public Task WarnAsync(string eventName, object? data = null) => WriteAsync("warn", eventName, data, null);
    public Task ErrorAsync(string eventName, Exception exception) => WriteAsync("error", eventName, null, exception);
    public Task ErrorAsync(string eventName, Exception exception, object? data) => WriteAsync("error", eventName, data, exception);

    public static string PrivateFileToken(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private async Task WriteAsync(string level, string eventName, object? data, Exception? exception)
    {
        var lockTaken = false;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            lockTaken = true;
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "quicksend.ndjson");
            if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
            {
                var archive = Path.Combine(_directory, $"quicksend-{DateTime.UtcNow:yyyyMMdd-HHmmss}.ndjson");
                File.Move(path, archive);
                foreach (var old in new DirectoryInfo(_directory).GetFiles("quicksend-*.ndjson").OrderByDescending(static f => f.CreationTimeUtc).Skip(4))
                    old.Delete();
            }

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow,
                level,
                eventName,
                data,
                errorType = exception?.GetType().FullName,
                error = exception?.ToString(),
                protocolVersion = Core.Protocol.ProtocolConstants.Version,
                appVersion = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString()
            };
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never take down the transfer engine.
        }
        finally
        {
            if (lockTaken) _gate.Release();
        }
    }
}
