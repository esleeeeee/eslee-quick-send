namespace Eslee.QuickSend.Core.Storage;

public static class SourcePathExpander
{
    public static SourcePathExpansion Expand(IReadOnlyList<string> paths)
    {
        var files = new List<ExpandedSource>();
        var issues = new List<SourcePathIssue>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                AddIssue(rawPath, "경로가 비어 있습니다.");
                continue;
            }

            string path;
            try { path = Path.GetFullPath(rawPath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                AddIssue(rawPath, "경로 형식이 올바르지 않습니다.");
                continue;
            }
            if (!seenInputs.Add(path)) continue;

            if (File.Exists(path))
            {
                AddFile(path, Path.GetFileName(path));
                continue;
            }
            if (!Directory.Exists(path))
            {
                AddIssue(path, "항목이 존재하지 않습니다.");
                continue;
            }

            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    AddIssue(path, "심볼릭 링크 또는 재분석 지점은 전송하지 않습니다.");
                    continue;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                AddIssue(path, "폴더에 접근할 수 없습니다.");
                continue;
            }

            var relativeRoot = Directory.GetParent(path)?.FullName ?? path;
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var directory))
            {
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(directory); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    AddIssue(directory, "폴더 내용을 읽을 수 없습니다.");
                    continue;
                }

                foreach (var entry in entries)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        AddIssue(entry, "항목 정보에 접근할 수 없습니다.");
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        AddIssue(entry, "심볼릭 링크 또는 재분석 지점은 전송하지 않습니다.");
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }
                    AddFile(entry, Path.GetRelativePath(relativeRoot, entry).Replace('\\', '/'));
                }
            }
        }

        return new SourcePathExpansion(files, issues);

        void AddFile(string filePath, string relativePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                info.Refresh();
                if (!info.Exists)
                {
                    AddIssue(filePath, "파일이 존재하지 않습니다.");
                    return;
                }
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    AddIssue(filePath, "심볼릭 링크 또는 재분석 지점은 전송하지 않습니다.");
                    return;
                }
                if (!seenFiles.Add(info.FullName)) return;
                files.Add(new ExpandedSource(info.FullName, relativePath, info.Length, info.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                AddIssue(filePath, "파일에 접근할 수 없습니다.");
            }
        }

        void AddIssue(string itemPath, string reason) => issues.Add(new SourcePathIssue(itemPath, reason));
    }
}

public sealed record ExpandedSource(string Path, string RelativePath, long Size, long ModifiedUtcTicks);
public sealed record SourcePathIssue(string Path, string Reason);
public sealed record SourcePathExpansion(IReadOnlyList<ExpandedSource> Files, IReadOnlyList<SourcePathIssue> Issues);
