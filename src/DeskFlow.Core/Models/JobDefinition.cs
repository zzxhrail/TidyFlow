namespace DeskFlow.Core.Models;

/// <summary>一次任务的完整描述，只包含数据，不含行为，方便序列化进配置或命令行参数。</summary>
public sealed class JobDefinition
{
    public required string InputPath { get; init; }

    public required string OutputDirectory { get; init; }

    /// <summary>输入源名称，null 表示使用目录默认源。</summary>
    public string? InputSource { get; init; }

    /// <summary>步骤名称列表，按顺序执行。</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>输出目标名称列表。</summary>
    public IReadOnlyList<string> Sinks { get; init; } = [];

    /// <summary>单条数据出错时是否继续处理剩余数据。</summary>
    public bool ContinueOnItemError { get; init; } = true;

    /// <summary>是否递归子目录。</summary>
    public bool Recurse { get; init; } = true;
}
