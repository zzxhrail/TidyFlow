using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Observers;

/// <summary>多路广播：同时写界面、控制台、文件日志，任何一路异常都不影响任务。</summary>
public sealed class CompositeObserver : IJobObserver
{
    private readonly IReadOnlyList<IJobObserver> _inner;

    public CompositeObserver(params IJobObserver[] inner) => _inner = inner;

    public void Log(JobLogLevel level, string message)
    {
        foreach (var observer in _inner)
        {
            try
            {
                observer.Log(level, message);
            }
            catch
            {
                // 观察者失败不应影响任务本身。
            }
        }
    }

    public void ReportProgress(JobProgress progress)
    {
        foreach (var observer in _inner)
        {
            try
            {
                observer.ReportProgress(progress);
            }
            catch
            {
            }
        }
    }
}

/// <summary>把回调包成观察者，WPF 层用它把日志推进 UI 线程。</summary>
public sealed class DelegateObserver : IJobObserver
{
    private readonly Action<JobLogLevel, string>? _log;
    private readonly Action<JobProgress>? _progress;

    public DelegateObserver(Action<JobLogLevel, string>? log = null, Action<JobProgress>? progress = null)
    {
        _log = log;
        _progress = progress;
    }

    public void Log(JobLogLevel level, string message) => _log?.Invoke(level, message);

    public void ReportProgress(JobProgress progress) => _progress?.Invoke(progress);
}
