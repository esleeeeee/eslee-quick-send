namespace Eslee.QuickSend.Core.Transfers;

public sealed record SourceFingerprint(long Size, long ModifiedUtcTicks, string? StableId)
{
    public static SourceFingerprint Capture(FileInfo file, string? stableId = null)
    {
        file.Refresh();
        if (!file.Exists)
            throw new FileNotFoundException("Source file no longer exists.", file.FullName);
        return new SourceFingerprint(file.Length, file.LastWriteTimeUtc.Ticks, stableId);
    }

    public bool Matches(FileInfo file, string? stableId = null)
    {
        file.Refresh();
        return file.Exists
            && file.Length == Size
            && file.LastWriteTimeUtc.Ticks == ModifiedUtcTicks
            && (StableId is null || string.Equals(StableId, stableId, StringComparison.Ordinal));
    }
}

