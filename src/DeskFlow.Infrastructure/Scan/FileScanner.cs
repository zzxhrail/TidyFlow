using System.Collections.Concurrent;

namespace DeskFlow.Infrastructure.Scan;

/// <summary>一条扫描结果。</summary>
public sealed record ScanItem(string Path, long SizeBytes, DateTime Modified)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? Path;

    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / 1024.0 / 1024:0.#} MB",
        _ => $"{SizeBytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

/// <summary>扫描条件。</summary>
public sealed class ScanOptions
{
    /// <summary>要扫的盘符或目录，例如 "D:\\"。</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>扩展名白名单（含点，小写）；空表示不限制。</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>只保留这个时间之后修改过的文件。</summary>
    public DateTime? ModifiedAfter { get; init; }

    /// <summary>只保留这个时间之前修改过的文件。</summary>
    public DateTime? ModifiedBefore { get; init; }

    /// <summary>小于这个字节数的文件跳过（默认 1 KB，过滤快捷方式/缩略图这类噪音）。</summary>
    public long MinimumBytes { get; init; } = 1024;

    /// <summary>最多返回多少条，防止整盘扫描把内存吃满。</summary>
    public int MaxResults { get; init; } = 5000;

    /// <summary>额外排除的目录（归档目录、配置目录等）。</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
}

/// <summary>扫描进度。</summary>
public sealed record ScanProgress(int ScannedDirectories, int Matched, string CurrentDirectory);

/// <summary>
/// 按盘符扫盘：跳过系统目录与程序目录，按扩展名 / 修改时间 / 大小筛选。
/// 只读遍历，不动任何文件。
/// </summary>
public static class FileScanner
{
    /// <summary>系统与程序目录：扫盘时永远不进（进去也只有几万个 dll）。</summary>
    private static readonly string[] ExcludedFolderNames =
    [
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "MSOCache",
        "node_modules", ".git", ".svn", "obj", "bin", "packages", "WinSxS",
        "Microsoft", "WindowsApps", "Temp", "tmp",
        "All Users", "Default", "Default User", "Config.Msi", "System32", "SysWOW64",
    ];

    public static IReadOnlyList<string> DefaultExcludedNames => ExcludedFolderNames;

    public static Task<IReadOnlyList<ScanItem>> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken);

    public static IReadOnlyList<ScanItem> Scan(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ScanItem>();
        var extensions = new HashSet<string>(
            options.Extensions.Select(static e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var excluded = new List<string>(options.ExcludedPaths);
        excluded.AddRange(options.Roots.Select(root => System.IO.Path.Combine(root, "Windows")));

        var scannedDirectories = 0;
        var stack = new Stack<string>(options.Roots.Where(Directory.Exists));

        while (stack.Count > 0 && results.Count < options.MaxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            if (IsExcluded(current, excluded))
            {
                continue;
            }

            scannedDirectories++;
            if (scannedDirectories % 40 == 0)
            {
                progress?.Report(new ScanProgress(scannedDirectories, results.Count, current));
            }

            IEnumerable<string> subdirectories;
            IEnumerable<string> files;
            try
            {
                subdirectories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in SafeEnumerate(files))
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists || info.Length < options.MinimumBytes)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (extensions.Count > 0 && !extensions.Contains(info.Extension))
                {
                    continue;
                }

                if (options.ModifiedAfter is { } after && info.LastWriteTime < after)
                {
                    continue;
                }

                if (options.ModifiedBefore is { } before && info.LastWriteTime > before)
                {
                    continue;
                }

                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    continue;
                }

                results.Add(new ScanItem(info.FullName, info.Length, info.LastWriteTime));
                if (results.Count >= options.MaxResults)
                {
                    break;
                }
            }

            foreach (var directory in SafeEnumerate(subdirectories))
            {
                var name = System.IO.Path.GetFileName(directory);
                if (ExcludedFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 跳过 junction / 符号链接：否则 All Users、Documents and Settings 这类
                // 重定向会把同一个目录扫很多遍，甚至绕成环
                try
                {
                    if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                stack.Push(directory);
            }
        }

        return results
            .OrderByDescending(static r => r.Modified)
            .ToList();
    }

    private static bool IsExcluded(string directory, IReadOnlyList<string> excluded)
    {
        foreach (var path in excluded)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (directory.StartsWith(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SafeEnumerate(IEnumerable<string> source)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            bool moved;
            try
            {
                moved = enumerator.MoveNext();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                yield break;
            }

            if (!moved)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }
}
