namespace DeskFlow.Core.Models;

public enum ArchiveStatus
{
    Pending = 0,
    Archived = 1,
    Ignored = 2,
    Failed = 3,

    /// <summary>已撤销归档：文件被放回原位置。</summary>
    Reverted = 4,

    /// <summary>用户主动删除了归档文件（走了回收站）。</summary>
    Deleted = 5,
}

/// <summary>一条文件记录：来源、归类、落盘位置、状态。面板与索引都读它。</summary>
public sealed class FileRecord
{
    public required string Id { get; init; }

    /// <summary>原始文件绝对路径（聊天工具目录里的原件）。</summary>
    public required string SourcePath { get; init; }

    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }

    public required FileSource Source { get; init; }

    public required string Category { get; set; }

    public ArchiveStatus Status { get; set; } = ArchiveStatus.Pending;

    /// <summary>归档后的绝对路径；未归档时为 null。</summary>
    public string? ArchivedPath { get; set; }

    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? ArchivedAt { get; set; }

    public string? Error { get; set; }

    public string? Hash { get; set; }

    /// <summary>为什么归到这一类：扩展名规则 / 内容识别（附带命中关键词）。</summary>
    public string? CategoryReason { get; set; }

    /// <summary>同一次批量归档的标记：短时间内、同一类别连续归档的文件共用一个批次。</summary>
    public string? BatchId { get; set; }

    /// <summary>撤销归档后文件实际落回的位置（原位置被占用时会改名）。</summary>
    public string? RestoredPath { get; set; }

    public string DisplayPath => ArchivedPath ?? SourcePath;

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();

    public string StatusText => Status switch
    {
        ArchiveStatus.Archived => "已归档",
        ArchiveStatus.Ignored => "已忽略",
        ArchiveStatus.Failed => "失败",
        ArchiveStatus.Reverted => "已撤销",
        ArchiveStatus.Deleted => "已删除",
        _ => "待接收",
    };
}