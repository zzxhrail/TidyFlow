namespace DeskFlow.Core.Models;

/// <summary>单个步骤对单条输入的执行结果。Ok=false 只影响本条数据，不会中断整批任务。</summary>
public sealed record StepOutcome(bool Ok, string? Error = null, string? Note = null)
{
    public static StepOutcome Success(string? note = null) => new(true, null, note);

    public static StepOutcome Failure(string error) => new(false, error);
}
