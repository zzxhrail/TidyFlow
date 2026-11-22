using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Watch;

/// <summary>
/// 轮询式监听器：每 N 秒扫一遍被监听目录，只有"这一轮和上一轮大小一致"的文件才算写完并上报。
/// 不用 FileSystemWatcher 的原因：网络盘、缓冲溢出、批量解压时会丢事件，轮询结果可复现、可单测。
/// </summary>
public sealed class IncomingWatcher : IDisposable
{
    private readonly List<WatchTarget> _targets = new();
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _sizeProbe = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public IncomingWatcher(IEnumerable<WatchTarget> targets, TimeSpan? interval = null)
    {
        // 不按"现在是否存在"过滤：客户端可能还没收到过文件，目录之后才创建，
        // 每轮扫描时 Enumerate 会自然跳过不存在的目录。
        _targets.AddRange(targets);
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>发现写完的新文件。</summary>
    public event Action<IncomingFile>? FileDetected;

    public IReadOnlyList<WatchTarget> Targets => _targets;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>把当前已存在的文件标记为已知，启动时不重复上报历史文件。</summary>
    public void PrimeExisting()
    {
        foreach (var file in EnumerateAll())
        {
            _known.Add(file);
            _sizeProbe[file] = SafeLength(file);
        }
    }

    public void MarkKnown(string fullPath)
    {
        _known.Add(fullPath);
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ScanOnce();
                }
                catch (Exception)
                {
                    // 单轮失败不能停掉监听。
                }

                try
                {
                    await Task.Delay(_interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>扫一轮，返回"本轮确认写入完成"的新文件。公开以便自检直接调用。</summary>
    public IReadOnlyList<IncomingFile> ScanOnce()
    {
        var found = new List<IncomingFile>();
        lock (_gate)
        {
            foreach (var target in _targets)
            {
                foreach (var file in Enumerate(target.Directory))
                {
                    if (_known.Contains(file) || IsNoise(file, target.Directory))
                    {
                        continue;
                    }

                    var length = SafeLength(file);
                    if (length < 0)
                    {
                        continue;
                    }

                    if (!_sizeProbe.TryGetValue(file, out var previous) || previous != length)
                    {
                        // 大小还在变，下一轮再看。
                        _sizeProbe[file] = length;
                        continue;
                    }

                    _known.Add(file);
                    _sizeProbe.Remove(file);
                    found.Add(new IncomingFile(file, target.Source, DateTimeOffset.Now));
                }
            }
        }

        foreach (var incoming in found)
        {
            FileDetected?.Invoke(incoming);
        }

        return found;
    }

    private IEnumerable<string> EnumerateAll() => _targets.SelectMany(t => Enumerate(t.Directory)).ToList();

    private static readonly HashSet<string> NoiseSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumb", "ThumbTemp", "Emoji", "emoji", "log", "logs", "cache", "Cache", "temp", "Temp",
        "nt_temp", "nt_db", "db", "db_storage", "resource_cache", "crash", "crashinfo", "dumps",
    };

    /// <summary>
    /// 聊天工具目录里混着缩略图、表情、日志、数据库缓存，这些不算"收到的文件"，
    /// 否则面板会被一堆 .db/.thumb 刷屏。
    /// </summary>
    public static bool IsNoise(string fullPath, string root)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // 最后一段是文件名，不参与判断
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (NoiseSegments.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Enumerate(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.Temporary,
            MaxRecursionDepth = 4,
        };

        try
        {
            return Directory.EnumerateFiles(directory, "*", options);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    public void Dispose() => Stop();
}