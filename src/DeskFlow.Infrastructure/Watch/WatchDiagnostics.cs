using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Watch;

/// <summary>一个被监听目录的探测结果。</summary>
public sealed record WatchProbe(
    string Name,
    string Path,
    FileSource Source,
    bool Exists,
    bool Detected,
    int RecentCount,
    string? LatestName,
    DateTime? LatestTime);

/// <summary>
/// 监听诊断：回答"到底是没监听到，还是客户端根本没写文件"。
/// 面板设置页与命令行 --diagnose 共用这一份逻辑，避免两处判断不一致。
/// </summary>
public static class WatchDiagnostics
{
    public static IReadOnlyList<WatchProbe> Probe(
        IEnumerable<WatchTarget> targets,
        TimeSpan window,
        int maxPerTarget = 20000,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<WatchProbe>();
        var since = DateTime.Now - window;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exists = Directory.Exists(target.Directory);
            var count = 0;
            string? latest = null;
            DateTime? latestTime = null;

            if (exists)
            {
                try
                {
                    var scanned = 0;
                    foreach (var file in Directory.EnumerateFiles(target.Directory, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MaxRecursionDepth = 4,
                    }))
                    {
                        if (++scanned > maxPerTarget)
                        {
                            break;
                        }

                        var info = new FileInfo(file);
                        if (info.LastWriteTime < since || IncomingWatcher.IsNoise(file, target.Directory))
                        {
                            continue;
                        }

                        count++;
                        if (latestTime is null || info.LastWriteTime > latestTime)
                        {
                            latestTime = info.LastWriteTime;
                            latest = info.Name;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    exists = false;
                }
            }

            rows.Add(new WatchProbe(target.Name, target.Directory, target.Source, exists, target.IsDetected, count, latest, latestTime));
        }

        return rows;
    }
}
