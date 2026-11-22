using DeskFlow.Core.Models;

namespace DeskFlow.Core.Abstractions;

/// <summary>进度与日志的出口。UI、控制台、文件日志都实现它，引擎只依赖这个接口。</summary>
public interface IJobObserver
{
    void Log(JobLogLevel level, string message);

    void ReportProgress(JobProgress progress);
}

/// <summary>什么都不做的观察者，用于测试与静默模式。</summary>
public sealed class NullJobObserver : IJobObserver
{
    public static readonly NullJobObserver Instance = new();

    public void Log(JobLogLevel level, string message)
    {
    }

    public void ReportProgress(JobProgress progress)
    {
    }
}
