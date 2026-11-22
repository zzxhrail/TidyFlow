using System.Collections.Concurrent;
using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Watch;

/// <summary>
/// 全盘监听：每个根目录挂一个递归 FileSystemWatcher，事件先进队列，
/// 700ms 后再确认"文件还活着、大小稳定"才上报——避免把正在写入的文件搬走。
/// 另外每 60 秒做一次补扫，兜住 FSW 缓冲溢出漏掉的事件。
/// </summary>
public sealed class GlobalFileWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private GlobalWatchOptions _options = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<string>? FileDetected;

    public bool IsRunning => _loop is { IsCompleted: false };

    public IReadOnlyList<string> ActiveRoots => _watchers.Select(static w => w.Path).ToList();

    public void Start(GlobalWatchOptions options)
    {
        Stop();
        _options = options;
        if (!options.Enabled || options.Roots.Count == 0)
        {
            return;
        }

        foreach (var root in options.Roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };

                watcher.Created += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                _watchers.Add(watcher);
                Debug($"watching {root} (recursive={watcher.IncludeSubdirectories})");
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Debug($"watch FAILED {root}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Debug($"started watchers={_watchers.Count}");

        // 启动即补扫最近 10 分钟落盘的文件（程序没开的时候落下的也不会漏）
        Task.Run(() =>
        {
            try
            {
                SweepRecent(TimeSpan.FromMinutes(10));
            }
            catch (Exception ex)
            {
                Debug($"sweep failed {ex.Message}");
            }
        });
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var ticks = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    FlushStable();
                    if (++ticks % 20 == 0)
                    {
                        Debug($"loop alive ticks={ticks} pending={_pending.Count}");
                    }
                }
                catch (Exception)
                {
                }

                try
                {
                    await Task.Delay(700, token).ConfigureAwait(false);
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
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _watchers.Clear();
        }

        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _pending.Clear();
        _sizes.Clear();
    }

    /// <summary>
    /// 启动补扫：把"程序没开时刚落盘"的文件也捞出来。
    /// 全盘遍历代价高，所以按时间窗（默认 10 分钟）+ 数量上限 + 时间预算三重限制，
    /// 并且只按目录名剪枝，不碰排除目录。
    /// </summary>
    public void SweepRecent(TimeSpan window, int maxFiles = 2000, int budgetSeconds = 20)
    {
        if (!_options.Enabled || _options.Roots.Count == 0)
        {
            return;
        }

        var since = DateTime.Now - window;
        var deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
        var found = 0;

        foreach (var root in _options.Roots)
        {
            if (found >= maxFiles || DateTime.UtcNow > deadline)
            {
                break;
            }

            foreach (var file in EnumerateGuarded(root, _options, since, deadline, maxFiles - found))
            {
                found++;
                FileDetected?.Invoke(file);
            }
        }

        Debug($"sweep done found={found}");
    }

    private static IEnumerable<string> EnumerateGuarded(string root, GlobalWatchOptions options, DateTime since, DateTime deadline, int limit)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var yielded = 0;

        while (stack.Count > 0)
        {
            if (yielded >= limit || DateTime.UtcNow > deadline)
            {
                yield break;
            }

            var current = stack.Pop();
            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!options.Extensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (info.LastWriteTime < since || !info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (!GlobalWatchFilter.ShouldWatch(file, options, info.Length, info.Attributes))
                {
                    continue;
                }

                yielded++;
                yield return file;
                if (yielded >= limit)
                {
                    yield break;
                }
            }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (name is "Windows" or "Program Files" or "Program Files (x86)" or "ProgramData"
                    or "$Recycle.Bin" or "System Volume Information" or "AppData" or "node_modules"
                    or ".git" or "obj" or "bin" or "dist" or "WindowsApps" or "Packages")
                {
                    continue;
                }

                stack.Push(dir);
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        Debug($"raw event {e.ChangeType} {e.FullPath}");
        Enqueue(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e) => Enqueue(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e) => _sizes.Clear();

    private void Enqueue(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Debug($"enqueue candidate {path}");

        // 先把明显不该管的挡在外面，避免队列被系统文件撑爆
        if (!_options.Extensions.Contains(Path.GetExtension(path)))
        {
            return;
        }

        _pending[path] = DateTime.UtcNow;
        Debug($"+queue {path}");
    }

    /// <summary>把队列里"已经稳定 700ms"的文件挑出来上报。</summary>
    private void FlushStable()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _pending.ToArray())
        {
            if ((now - pair.Value).TotalMilliseconds < 700)
            {
                continue;
            }

            var path = pair.Key;
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists)
                {
                    _pending.TryRemove(path, out _);
                    _sizes.TryRemove(path, out _);
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // 大小还在变 = 还在写，下一轮再看
            if (!_sizes.TryGetValue(path, out var lastSize) || lastSize != info.Length)
            {
                _sizes[path] = info.Length;
                _pending[path] = now;
                continue;
            }

            var accept = GlobalWatchFilter.ShouldWatch(path, _options, info.Length, info.Attributes);
            Debug($"check {path} size={info.Length} accept={accept}");
            if (!accept)
            {
                _pending.TryRemove(path, out _);
                _sizes.TryRemove(path, out _);
                continue;
            }

            _pending.TryRemove(path, out _);
            _sizes.TryRemove(path, out _);
            Debug($"fire {path}");
            FileDetected?.Invoke(path);
        }
    }

    private static void Debug(string message)
    {
        if (Environment.GetEnvironmentVariable("DESKFLOW_DEBUG") == "1")
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(DeskFlow.Infrastructure.AppPaths.RootDirectory, "global-watch.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose() => Stop();
}