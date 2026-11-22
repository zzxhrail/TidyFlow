using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Core.Abstractions;

/// <summary>输出目标：把整批处理结果写成报表、文件、接口调用或数据库记录。</summary>
public interface IOutputSink
{
    string Name { get; }

    string Description { get; }

    Task<IReadOnlyList<Artifact>> WriteAsync(
        JobContext context,
        IReadOnlyList<ProcessedItem> items,
        CancellationToken cancellationToken);
}
