using Eslee.QuickSend.Windows.Diagnostics;
using Eslee.QuickSend.Windows.Discovery;
using Eslee.QuickSend.Windows.Persistence;
using Eslee.QuickSend.Windows.Security;
using Eslee.QuickSend.Windows.Transfers;

namespace Eslee.QuickSend.Windows;

public static class AppServices
{
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var pointer);
        try
        {
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer) ?? throw new IOException("Downloads folder is unavailable.");
        }
        finally { if (pointer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pointer); }
    }

    private static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eslee", "QuickSend");
    public static string ReceiveDirectory { get; } = Path.Combine(DownloadsFolder(), "eslee QuickSend");
    public static DiagnosticLog Log { get; } = new(Path.Combine(DataDirectory, "logs"));
    public static AppDatabase Database { get; } = new(Path.Combine(DataDirectory, "quicksend.db"));
    public static SqliteTransferStore TransferStore { get; } = new(Database);
    public static TrustedDeviceStore Trust { get; } = new(Database);
    public static DeviceIdentityService Identity { get; } = new(Database);
    public static DeviceNameService DeviceName { get; } = new(Database);
    public static MdnsDiscoveryService Discovery { get; } = new();
    public static TransferCoordinator Coordinator { get; } = new(TransferStore, Trust, Identity, DeviceName, Discovery, Log, ReceiveDirectory);
    public static HistoryService History { get; } = new(Database);
    public static Updates.UpdateCheckService Updates { get; } = new(Database, Log);
    public static Startup.AutoStartService AutoStart { get; } =
        new(Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty);
    private static readonly SemaphoreSlim InitializationGate = new(1, 1);
    private static bool _initialized;
    private static bool _disposed;

    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await InitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            ObjectDisposedException.ThrowIf(_disposed, typeof(AppServices));

            await RunStageAsync("directories", () =>
            {
                Directory.CreateDirectory(DataDirectory);
                Directory.CreateDirectory(ReceiveDirectory);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            await RunStageAsync("database", () => Database.InitializeAsync(cancellationToken)).ConfigureAwait(false);
            DeviceIdentity identity = null!;
            await RunStageAsync("identity", async () => identity = await Identity.GetOrCreateAsync(cancellationToken)).ConfigureAwait(false);
            var deviceName = string.Empty;
            await RunStageAsync("device-name", async () => deviceName = await DeviceName.LoadAsync(cancellationToken)).ConfigureAwait(false);
            // The advertised endpoint must already be accepting connections when it becomes visible over DNS-SD.
            await RunStageAsync("coordinator", Coordinator.StartAsync).ConfigureAwait(false);
            await RunStageAsync("mdns", () => Discovery.StartAsync(identity.DeviceId, deviceName,
                identity.Fingerprint, Core.Protocol.ProtocolConstants.DefaultPort)).ConfigureAwait(false);
            // A rename only rewrites the TXT name; the device id and fingerprint are unchanged.
            DeviceName.NameChanged += (_, updated) => _ = Discovery.UpdateDeviceNameAsync(updated);
            Coordinator.StartRecovery();
            // Fire-and-forget: the update check must never delay startup or transfers.
            Updates.Start();
            _initialized = true;
        }
        finally
        {
            InitializationGate.Release();
        }
    }

    public static async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Log.InfoAsync("services.shutdown.start").ConfigureAwait(false);
        try
        {
            Updates.Dispose();
        }
        catch (Exception ex)
        {
            await Log.ErrorAsync("services.updates.dispose.failed", ex).ConfigureAwait(false);
        }

        try
        {
            Coordinator.Dispose();
        }
        catch (Exception ex)
        {
            await Log.ErrorAsync("services.coordinator.dispose.failed", ex).ConfigureAwait(false);
        }

        try
        {
            Discovery.Dispose();
        }
        catch (Exception ex)
        {
            await Log.ErrorAsync("services.discovery.dispose.failed", ex).ConfigureAwait(false);
        }
        await Log.InfoAsync("services.shutdown.complete").ConfigureAwait(false);
    }

    private static async Task RunStageAsync(string stage, Func<Task> action)
    {
        await Log.InfoAsync("initialization.stage.start", new { stage }).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
            await Log.InfoAsync("initialization.stage.complete", new { stage }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Log.ErrorAsync($"initialization.{stage}.failed", ex).ConfigureAwait(false);
            throw;
        }
    }
}
