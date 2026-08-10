using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Eslee.QuickSend.Windows.Diagnostics;
using Eslee.QuickSend.Windows.Persistence;

namespace Eslee.QuickSend.Windows.Updates;

/// <summary>A point-in-time answer to "is a newer official release available?".</summary>
public sealed record UpdateStatus(
    UpdateStatusKind Kind,
    Version CurrentVersion,
    Version? LatestVersion,
    string? LatestTag,
    string? ReleaseUrl,
    DateTimeOffset? CheckedAt);

/// <summary>
/// Compares the running version against the newest official GitHub release.
/// </summary>
/// <remarks>
/// Entirely isolated from the transfer engine: every failure is swallowed and logged,
/// results are cached in the settings table so a fresh network request is made at most
/// once per <see cref="UpdateCheckPolicy.CheckInterval"/>, and a manual check from the
/// settings dialog always bypasses the gate. Draft and prerelease entries never appear
/// because the <c>releases/latest</c> endpoint excludes them, with a defensive re-check
/// in <see cref="UpdateCheckPolicy.ParseLatestRelease"/>. This service never downloads
/// or installs anything.
/// </remarks>
public sealed class UpdateCheckService : IDisposable
{
    private const string LatestReleaseApi = "https://api.github.com/repos/esleeeeee/eslee-quick-send/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/esleeeeee/eslee-quick-send/releases";

    private const string LastCheckedKey = "update.last_checked_utc";
    private const string LatestTagKey = "update.latest_tag";
    private const string LatestUrlKey = "update.latest_url";

    private readonly AppDatabase _database;
    private readonly DiagnosticLog _log;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly HttpClient _http;
    private Timer? _timer;
    private int _disposed;

    public UpdateCheckService(AppDatabase database, DiagnosticLog log)
    {
        _database = database;
        _log = log;
        CurrentVersion = ResolveCurrentVersion();
        Status = new UpdateStatus(UpdateStatusKind.Unknown, CurrentVersion, null, null, null, null);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"eslee-QuickSend/{CurrentVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public Version CurrentVersion { get; }
    public string CurrentVersionText => CurrentVersion.ToString(3);
    public UpdateStatus Status { get; private set; }

    /// <summary>Raised on a background thread; UI subscribers must marshal themselves.</summary>
    public event EventHandler<UpdateStatus>? StatusChanged;

    /// <summary>
    /// Restores the cached result so the UI has an answer immediately, then starts the
    /// background cadence: one gated check now and a periodic re-check while running.
    /// Never blocks startup and never throws.
    /// </summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadCachedStatusAsync().ConfigureAwait(false);
                await CheckAsync(force: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("update.check.start.failed", new { error = ex.GetType().Name });
            }
        });
        // The six-hour tick only re-fires the gated check, so the network is still hit
        // at most once per CheckInterval even though the timer runs more often.
        _timer = new Timer(
            _ => _ = CheckAsync(force: false),
            null,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(6));
    }

    /// <summary>
    /// Runs one update check. <paramref name="force"/> is the manual button: it ignores
    /// the freshness gate. Returns the resulting status and never throws.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return Status;
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!force)
            {
                var lastChecked = await ReadCachedTimeAsync(cancellationToken).ConfigureAwait(false);
                if (!UpdateCheckPolicy.ShouldCheck(lastChecked, now))
                {
                    _log.Info("update.check.skipped_fresh", new { lastChecked });
                    return Status;
                }
            }

            _log.Info("update.check.begin", new { force, current = CurrentVersionText });
            try
            {
                using var response = await _http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // No official release exists at all, so there is nothing to update to.
                    await PersistAsync(now, null, null, cancellationToken).ConfigureAwait(false);
                    Publish(new UpdateStatus(UpdateStatusKind.UpToDate, CurrentVersion, null, null, null, now));
                    _log.Info("update.check.completed", new { result = "no_official_release" });
                    return Status;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var latest = UpdateCheckPolicy.ParseLatestRelease(json);
                if (latest is null)
                {
                    if (Status.Kind is UpdateStatusKind.Unknown or UpdateStatusKind.CheckFailed)
                        Publish(Status with { Kind = UpdateStatusKind.CheckFailed, CheckedAt = now });
                    _log.Warn("update.check.completed", new { result = "unparseable_payload" });
                    return Status;
                }

                await PersistAsync(now, latest.TagName, latest.HtmlUrl, cancellationToken).ConfigureAwait(false);
                var kind = UpdateCheckPolicy.Classify(CurrentVersion, latest.Version);
                Publish(new UpdateStatus(kind, CurrentVersion, latest.Version, latest.TagName, latest.HtmlUrl, now));
                _log.Info("update.check.completed", new { result = kind.ToString(), latest = latest.TagName });
            }
            catch (Exception ex)
            {
                // Offline, DNS failure, rate limit, timeout: transfers are unaffected. A
                // transient failure must not erase a cached UpdateAvailable answer, so
                // CheckFailed is only published when no real result exists yet.
                if (Status.Kind is UpdateStatusKind.Unknown or UpdateStatusKind.CheckFailed)
                    Publish(Status with { Kind = UpdateStatusKind.CheckFailed, CheckedAt = now });
                else
                    StatusChanged?.Invoke(this, Status);
                _log.Warn("update.check.failed", new { error = ex.GetType().Name });
            }
            return Status;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task LoadCachedStatusAsync()
    {
        var lastChecked = await ReadCachedTimeAsync(CancellationToken.None).ConfigureAwait(false);
        var tag = await _database.GetSettingAsync(LatestTagKey).ConfigureAwait(false);
        var url = await _database.GetSettingAsync(LatestUrlKey).ConfigureAwait(false);
        if (lastChecked is null) return;
        var latest = UpdateCheckPolicy.TryParseTag(tag);
        var kind = latest is null
            ? UpdateStatusKind.UpToDate
            : UpdateCheckPolicy.Classify(CurrentVersion, latest);
        Publish(new UpdateStatus(kind, CurrentVersion, latest, tag, url, lastChecked));
        _log.Info("update.check.cache_restored", new { tag, lastChecked });
    }

    private async Task<DateTimeOffset?> ReadCachedTimeAsync(CancellationToken cancellationToken)
    {
        var raw = await _database.GetSettingAsync(LastCheckedKey, cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    private async Task PersistAsync(DateTimeOffset checkedAt, string? tag, string? url, CancellationToken cancellationToken)
    {
        try
        {
            await _database.SetSettingAsync(LastCheckedKey, checkedAt.ToString("O"), cancellationToken).ConfigureAwait(false);
            await _database.SetSettingAsync(LatestTagKey, tag ?? string.Empty, cancellationToken).ConfigureAwait(false);
            await _database.SetSettingAsync(LatestUrlKey, url ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("update.check.cache_write.failed", new { error = ex.GetType().Name });
        }
    }

    private void Publish(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private static Version ResolveCurrentVersion()
    {
        var informational = typeof(UpdateCheckService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (UpdateCheckPolicy.NormalizeCurrentVersion(informational) is { } fromInformational)
            return fromInformational;

        // AssemblyVersion is pinned to 1.0.0.0 for compatibility, so the file version is
        // the correct fallback, not the assembly name.
        try
        {
            if (Environment.ProcessPath is { } path &&
                Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var fromFile))
                return new Version(fromFile.Major, fromFile.Minor, fromFile.Build);
        }
        catch (Exception)
        {
            // Fall through to the sentinel below.
        }
        return new Version(0, 0, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        _http.Dispose();
        _checkGate.Dispose();
    }
}
