using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Core.Abstractions;

/// <summary>
/// 处理步骤。约束：只读写 ProcessedItem，不直接改磁盘文件；
/// 需要落盘的动作放到 IOutputSink，保证"处理"和"写出"两侧可分别测试。
/// </summary>
public interface IPipelineStep
{
    string Name { get; }

    string Description { get; }

    Task<StepOutcome> ExecuteAsync(ProcessedItem item, JobContext context, CancellationToken cancellationToken);
}
