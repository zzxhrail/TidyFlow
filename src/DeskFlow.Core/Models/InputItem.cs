namespace DeskFlow.Core.Models;

/// <summary>被处理的一条输入记录。路径一律保持绝对路径 + 相对根目录的路径，便于报表阅读。</summary>
public sealed record InputItem(
    string FullPath,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc)
{
    public string FileName => Path.GetFileName(FullPath);

    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();
}
