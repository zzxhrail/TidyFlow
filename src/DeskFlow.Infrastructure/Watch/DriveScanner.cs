namespace DeskFlow.Infrastructure.Watch;

/// <summary>
/// 兜底扫描：当客户端把数据放到非默认位置（改过"文件保存目录"、装在别的盘）时，
/// 用有限深度扫盘找特征目录。结果应缓存进配置，不要每次启动都扫。
/// </summary>
public static class DriveScanner
{
    private static readonly string[] Markers = ["xwechat_files", "WeChat Files", "Tencent Files", "DingTalk"];

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information",
        "node_modules", "AppData", ".git", "WindowsApps", "Recovery", "PerfLogs", "MSOCache",
    };

    /// <param name="maxDepth">从盘符根开始的层数，3 层足够覆盖 D:\docu\Tencent Files 这类。</param>
    public static IReadOnlyList<string> Scan(int maxDepth = 3, CancellationToken cancellationToken = default)
    {
        var found = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            {
                continue;
            }

            Walk(drive.RootDirectory.FullName, 0, maxDepth, found, cancellationToken);
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(string directory, int depth, int maxDepth, List<string> found, CancellationToken cancellationToken)
    {
        if (depth > maxDepth)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (Markers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(child);
                continue;
            }

            if (SkipNames.Contains(name))
            {
                continue;
            }

            Walk(child, depth + 1, maxDepth, found, cancellationToken);
        }
    }
}
