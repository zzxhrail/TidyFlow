namespace DeskFlow.Core.Models;

/// <summary>归档策略。默认复制（原件留在聊天工具目录，避免对方客户端找不到文件）。</summary>
public sealed class ArchiveOptions
{
    public required string ArchiveRoot { get; init; }

    public bool MoveInsteadOfCopy { get; init; }

    /// <summary>同名同内容视为重复，直接跳过并不覆盖。</summary>
    public bool SkipDuplicates { get; init; } = true;

    /// <summary>是否启用"按内容分类"（读正文关键词判断类别），默认开启。</summary>
    public bool UseContentClassification { get; init; } = true;

    /// <summary>强制归档到指定类别（拖拽到某个分类上时用）；为 null 则按内容/扩展名自动分类。</summary>
    public string? CategoryOverride { get; init; }
}

public sealed record ArchiveOutcome(FileRecord Record, bool Success, bool Duplicate, string? Error)
{
    public static ArchiveOutcome Ok(FileRecord record) => new(record, true, false, null);

    public static ArchiveOutcome Duplicated(FileRecord record) => new(record, true, true, null);

    public static ArchiveOutcome Failed(FileRecord record, string error) => new(record, false, false, error);
}