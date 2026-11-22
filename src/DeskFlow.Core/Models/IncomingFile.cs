namespace DeskFlow.Core.Models;

/// <summary>监听器发现的一个新文件（已确认写完、可安全读取）。</summary>
public sealed record IncomingFile(string FullPath, FileSource Source, DateTimeOffset DetectedAt);

/// <summary>被监听的目录：聊天工具的收文件目录或用户手动添加的目录。</summary>
public sealed record WatchTarget(string Name, string Directory, FileSource Source, bool IsDetected)
{
    public bool Exists => System.IO.Directory.Exists(Directory);
}
