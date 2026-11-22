namespace DeskFlow.Core.Models;

public enum JobLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed record JobProgress(int Completed, int Total, string Phase)
{
    public double Percent => Total <= 0 ? 0 : Math.Round(Completed * 100.0 / Total, 1);
}

/// <summary>任务结束后的唯一事实来源。界面、命令行、单元检查都只读它。</summary>
public sealed record JobReport(
    string RunId,
    bool Succeeded,
    bool Canceled,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    int ItemCount,
    int FailedCount,
    IReadOnlyList<Artifact> Artifacts,
    IReadOnlyList<string> Messages);
