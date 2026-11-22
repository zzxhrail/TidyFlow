using DeskFlow.Core.Abstractions;

namespace DeskFlow.Core.Runtime;

/// <summary>一次任务运行的上下文：路径、观察者、配置。步骤与输出都从这里取依赖，不自己 new。</summary>
public sealed class JobContext
{
    public required string RunId { get; init; }

    public required string InputRoot { get; init; }

    public required string OutputDirectory { get; init; }

    public required IJobObserver Observer { get; init; }

    public required ISettingsStore Settings { get; init; }

    public bool Recurse { get; init; } = true;

    public void LogInfo(string message) => Observer.Log(Models.JobLogLevel.Info, message);

    public void LogWarn(string message) => Observer.Log(Models.JobLogLevel.Warn, message);

    public void LogError(string message) => Observer.Log(Models.JobLogLevel.Error, message);
}
