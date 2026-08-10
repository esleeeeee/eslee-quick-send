using System.Text.Json;

namespace Eslee.QuickSend.Windows.Updates;

public enum UpdateStatusKind
{
    /// <summary>No check has completed yet and no cached result exists.</summary>
    Unknown,
    UpToDate,
    UpdateAvailable,
    CheckFailed,
}

/// <summary>The newest official release as reported by GitHub.</summary>
public sealed record LatestRelease(Version Version, string TagName, string HtmlUrl);

/// <summary>
/// Pure decision logic for the update check: tag parsing, release-payload parsing,
/// version comparison and the re-check interval gate. No IO lives here so the whole
/// policy is exercised by the console test harness.
/// </summary>
public static class UpdateCheckPolicy
{
    /// <summary>How long a completed check stays fresh before a background re-check.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Parses a release tag such as <c>v0.0.3</c> into a <see cref="Version"/>.
    /// Returns <c>null</c> for anything that is not a plain numeric version.
    /// </summary>
    public static Version? TryParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        // Version.TryParse requires at least two components; a bare major like "1" is
        // not a tag shape this repository uses, so rejecting it is fine.
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// Normalizes the version the app was built with. The SDK appends the source
    /// revision to the informational version (<c>0.0.3+abc123</c>), which must be
    /// stripped before it can be compared numerically.
    /// </summary>
    public static Version? NormalizeCurrentVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var text = informationalVersion.Trim();
        var metadata = text.IndexOfAny(['+', '-']);
        if (metadata > 0) text = text[..metadata];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// Parses the GitHub <c>releases/latest</c> JSON payload. Returns <c>null</c> when
    /// the payload is malformed, or — defensively — when it is marked draft or
    /// prerelease, even though the endpoint itself already excludes both.
    /// </summary>
    public static LatestRelease? ParseLatestRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) return null;

            var tag = tagElement.GetString();
            var version = TryParseTag(tag);
            if (version is null || tag is null) return null;

            var url = root.TryGetProperty("html_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;
            return new LatestRelease(version, tag, url ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a background check should actually hit the network. A manual check
    /// bypasses this gate entirely. A last-checked time in the future (clock change)
    /// counts as stale so the cache cannot wedge the checker.
    /// </summary>
    public static bool ShouldCheck(DateTimeOffset? lastChecked, DateTimeOffset now, TimeSpan? interval = null)
    {
        if (lastChecked is not { } last) return true;
        var window = interval ?? CheckInterval;
        if (last > now + TimeSpan.FromMinutes(5)) return true;
        return now - last >= window;
    }

    /// <summary>An update exists only when the official release is strictly newer.</summary>
    public static UpdateStatusKind Classify(Version current, Version latest) =>
        latest > current ? UpdateStatusKind.UpdateAvailable : UpdateStatusKind.UpToDate;
}
