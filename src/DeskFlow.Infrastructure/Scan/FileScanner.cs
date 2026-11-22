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

    /// <summary>小于这个字节数的文件跳过（默认 1 KB）。</summary>
    public long MinimumBytes { get; init; } = 1024;

    /// <summary>最多返回多少条，防止整盘扫描把内存吃满。</summary>
    public int MaxResults { get; init; } = 5000;

    /// <summary>最多进多少个目录。</summary>
    public int MaxDirectories { get; init; } = 300_000;

    /// <summary>额外排除的目录（归档目录、配置目录等）。</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
}

/// <summary>扫描进度。</summary>
public sealed record ScanProgress(int ScannedDirectories, int Matched, string CurrentDirectory);

/// <summary>
/// 按盘符扫盘：跳过系统目录与程序目录，按扩展名 / 修改时间 / 大小筛选。只读遍历。
/// 性能要点：先用字符串过滤扩展名再取 FileInfo（省掉绝大多数 stat）；
/// 枚举用 EnumerationOptions 一次跳过隐藏/系统/重解析点与无权限目录（异常在整盘扫描里很贵）。
/// </summary>
public static class FileScanner
{
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
        var results = new List<ScanItem>(Math.Min(options.MaxResults, 4096));
        var extensions = new HashSet<string>(
            options.Extensions.Select(static e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var excludedRoots = options.ExcludedPaths
            .Where(static e => !string.IsNullOrWhiteSpace(e))
            .Select(static e => e.TrimEnd('\\'))
            .ToList();
        excludedRoots.AddRange(options.Roots.Select(root => System.IO.Path.Combine(root, "Windows").TrimEnd('\\')));

        var directoryOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };

        var fileOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };

        var excludedNames = new HashSet<string>(ExcludedFolderNames, StringComparer.OrdinalIgnoreCase);
        var scannedDirectories = 0;
        var stack = new Stack<string>(options.Roots.Where(Directory.Exists));
        var lastReport = DateTime.MinValue;

        while (stack.Count > 0 && results.Count < options.MaxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            if (IsUnderAny(current, excludedRoots))
            {
                continue;
            }

            scannedDirectories++;
            if (scannedDirectories > options.MaxDirectories)
            {
                break;
            }

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 250)
            {
                lastReport = now;
                progress?.Report(new ScanProgress(scannedDirectories, results.Count, current));
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*", fileOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (extensions.Count > 0 && !extensions.Contains(System.IO.Path.GetExtension(file)))
                    {
                        continue;
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(file);
                        if (!info.Exists || info.Length < options.MinimumBytes)
                        {
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
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

                    results.Add(new ScanItem(info.FullName, info.Length, info.LastWriteTime));
                    if (results.Count >= options.MaxResults)
                    {
                        break;
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(current, "*", directoryOptions))
                {
                    var name = System.IO.Path.GetFileName(directory);
                    if (excludedNames.Contains(name))
                    {
                        continue;
                    }

                    stack.Push(directory);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 无权限 / 设备异常：跳过继续
            }
        }

        results.Sort(static (a, b) => b.Modified.CompareTo(a.Modified));
        return results;
    }

    private static bool IsUnderAny(string directory, IReadOnlyList<string> trimmedRoots)
    {
        foreach (var path in trimmedRoots)
        {
            if (directory.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
