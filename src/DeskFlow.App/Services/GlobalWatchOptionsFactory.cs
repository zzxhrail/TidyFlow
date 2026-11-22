using System.IO;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Settings;
using DeskFlow.Infrastructure.Watch;

namespace DeskFlow.App.Services;

/// <summary>把设置里的全局监听配置翻译成监听器要的选项（含默认值与排除项）。</summary>
public static class GlobalWatchOptionsFactory
{
    public static GlobalWatchOptions Build(
        ArchiveSettings settings,
        string? installDirectory = null,
        IEnumerable<string>? extraExcluded = null)
    {
        var defaults = GlobalWatchFilter.Default();

        var roots = settings.GlobalRoots.Count > 0 ? settings.GlobalRoots : defaults.Roots;
        var extensions = settings.GlobalExtensions.Count > 0
            ? settings.GlobalExtensions
            : GlobalWatchFilter.DefaultExtensions.ToList();

        // 绝不能把我们自己的东西搬走：归档根、配置目录、安装目录
        var excluded = new List<string>
        {
            settings.ArchiveRoot,
            AppPaths.RootDirectory,
            AppPaths.LogDirectory,
        };

        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            excluded.Add(installDirectory);
        }

        // 聊天工具目录已经有专门的监听器，全局监听不重复处理，避免同一文件归档两次
        if (extraExcluded is not null)
        {
            excluded.AddRange(extraExcluded.Where(static p => !string.IsNullOrWhiteSpace(p)));
        }

        return new GlobalWatchOptions
        {
            Enabled = settings.GlobalWatchEnabled,
            Roots = roots.Where(Directory.Exists).ToList(),
            Extensions = new HashSet<string>(Normalize(extensions), StringComparer.OrdinalIgnoreCase),
            ExcludedPaths = excluded.Where(static p => !string.IsNullOrWhiteSpace(p)).ToList(),
        };
    }

    private static IEnumerable<string> Normalize(IEnumerable<string> extensions) =>
        extensions
            .Select(static e => (e ?? string.Empty).Trim().ToLowerInvariant())
            .Select(static e => e.Length == 0 ? e : (e[0] == '.' ? e : "." + e))
            .Where(static e => e.Length > 1);
}