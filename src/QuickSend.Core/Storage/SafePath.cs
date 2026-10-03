namespace Eslee.QuickSend.Core.Storage;

public static class SafePath
{
    public static string ResolveUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new IOException("Absolute paths are not accepted from a remote manifest.");

        var normalized = relativePath.Replace('\\', '/');
        foreach (var part in normalized.Split('/'))
        {
            var stem = part.Split('.')[0];
            if (string.IsNullOrEmpty(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl) ||
                new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789\u00B9\u00B2\u00B3".Contains(stem[3])))
                throw new IOException("Unsafe remote path component.");
        }
        normalized = normalized.Replace('/', Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The remote path escapes the configured receive directory.");
        RejectReparsePoints(candidate);
        return candidate;
    }

    public static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Receive paths cannot traverse links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string ChooseNonConflictingPath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
            return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }
}
