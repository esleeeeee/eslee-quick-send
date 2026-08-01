using Eslee.QuickSend.Core.Devices;

namespace Eslee.QuickSend.Windows.Persistence;

public sealed record DeviceNameUpdateResult(bool Accepted, string Name, string? Error);

/// <summary>
/// Stores the QuickSend display name for this PC.
/// </summary>
/// <remarks>
/// Only the display name is persisted. <c>device.id</c> and the identity certificate are
/// never rewritten here, so renaming keeps the fingerprint, trusted-device rows and the
/// existing SAS pairing intact.
/// </remarks>
public sealed class DeviceNameService(AppDatabase database)
{
    public const string SettingKey = "device.name";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _current = DeviceNameRules.NormalizeOrFallback(Environment.MachineName, "QuickSend PC");

    /// <summary>Last known name; safe to read from the UI thread.</summary>
    public string Current => Volatile.Read(ref _current);

    public static string PlatformFallback => DeviceNameRules.NormalizeOrFallback(Environment.MachineName, "QuickSend PC");

    public event EventHandler<string>? NameChanged;

    public async ValueTask<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await database.GetSettingAsync(SettingKey, cancellationToken);
        var resolved = DeviceNameRules.NormalizeOrFallback(stored, PlatformFallback);
        Volatile.Write(ref _current, resolved);
        return resolved;
    }

    public async ValueTask<DeviceNameUpdateResult> SetAsync(string? candidate, CancellationToken cancellationToken = default)
    {
        if (!DeviceNameRules.TryNormalize(candidate, out var normalized, out var error))
            return new DeviceNameUpdateResult(false, Current, error);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await database.SetSettingAsync(SettingKey, normalized, cancellationToken);
            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _gate.Release();
        }

        NameChanged?.Invoke(this, normalized);
        return new DeviceNameUpdateResult(true, normalized, null);
    }
}
