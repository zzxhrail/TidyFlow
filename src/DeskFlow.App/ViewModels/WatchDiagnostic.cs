namespace DeskFlow.App.ViewModels;

/// <summary>一个被监听目录的诊断结果，直接展示给人看。</summary>
public sealed record WatchDiagnostic(
    string Name,
    string Path,
    bool Exists,
    bool Detected,
    int RecentFileCount,
    string? LatestFile,
    DateTime? LatestTime)
{
    public string StateText => Exists
        ? (RecentFileCount > 0 ? $"24h 内 {RecentFileCount} 个文件" : "目录存在，24h 内没有新文件")
        : "目录不存在（客户端还没落过盘）";

    public string LatestText => LatestFile is null
        ? "—"
        : $"{LatestFile}  ({LatestTime:HH:mm})";

    public string Display => $"[{StateText}] {Name}  —  {Path}\n    最新: {LatestText}";
}
