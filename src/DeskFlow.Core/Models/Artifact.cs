namespace DeskFlow.Core.Models;

/// <summary>输出产物。Kind 用于界面分组，例如 csv / json / log。</summary>
public sealed record Artifact(string Path, string Kind, long Bytes);
