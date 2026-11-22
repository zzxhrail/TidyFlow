using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Watch;

/// <summary>全局监听的配置：看哪些盘、收哪些类型。</summary>
public sealed class GlobalWatchOptions
{
    public bool Enabled { get; init; }

    public IReadOnlyList<string> Roots { get; init; } = [];

    /// <summary>要处理的扩展名（含点）。空集合 = 什么都不收（避免误伤整机文件）。</summary>
    public IReadOnlySet<string> Extensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
}

/// <summary>
/// 全局监听的过滤规则：什么算"用户想要的文件"。
/// 纯函数，方便单测——整机扫描的误伤成本很高，规则必须可验证。
/// </summary>
public static class GlobalWatchFilter
{
    /// <summary>默认收这些类型：文档、表格、演示、图片、压缩包。</summary>
    public static readonly string[] DefaultExtensions =
    [
        ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".pdf", ".txt", ".csv", ".md",
        ".png", ".jpg", ".jpeg", ".zip", ".rar", ".7z",
    ];

    private static readonly string[] ExcludedSegments =
    [
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "AppData", "node_modules", ".git", ".vs", "obj", "bin",
        "dist", "Recovery", "PerfLogs", "WindowsApps", "OneDriveTemp", "Temp", "tmp",
        "Microsoft", "Packages", "WpSystem", "WUDownloadCache", "SoftwareDistribution",
    ];

    public static GlobalWatchOptions Default() => new()
    {
        Enabled = false,
        Roots = DriveInfo.GetDrives()
            .Where(static d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(static d => d.RootDirectory.FullName)
            .ToList(),
        Extensions = new HashSet<string>(DefaultExtensions, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// 判断一个刚落盘的文件要不要处理。
    /// </summary>
    /// <param name="fullPath">文件绝对路径。</param>
    /// <param name="options">全局监听配置。</param>
    /// <param name="fileSize">文件大小，0 字节的占位文件跳过。</param>
    /// <param name="attributes">文件属性，隐藏/系统文件跳过。</param>
    public static bool ShouldWatch(string fullPath, GlobalWatchOptions options, long fileSize, FileAttributes attributes)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(fullPath) || fileSize <= 0)
        {
            return false;
        }

        if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System) ||
            attributes.HasFlag(FileAttributes.Temporary))
        {
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(extension) || !options.Extensions.Contains(extension))
        {
            return false;
        }

        // 常见临时命名：~$xxx.docx、xxx.tmp、xxx.part、xxx.crdownload
        var name = Path.GetFileName(fullPath);
        if (name.StartsWith("~$", StringComparison.Ordinal) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var excluded in options.ExcludedPaths)
        {
            if (!string.IsNullOrWhiteSpace(excluded) && IsUnder(fullPath, excluded))
            {
                return false;
            }
        }

        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            if (ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsUnder(string fullPath, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
