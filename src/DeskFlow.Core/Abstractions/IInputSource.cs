using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Core.Abstractions;

/// <summary>输入源适配器：把外部世界（目录 / 命令行 / 接口 / 数据库）变成 InputItem 列表。</summary>
public interface IInputSource
{
    string Name { get; }

    string Description { get; }

    Task<IReadOnlyList<InputItem>> CollectAsync(JobContext context, CancellationToken cancellationToken);
}
