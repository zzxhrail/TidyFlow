using System.Diagnostics;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;

namespace DeskFlow.Core.Runtime;

/// <summary>
/// 任务引擎：只有一条主流程 —— 收集 → 逐条跑步骤 → 整批写出 → 返回报告。
/// 它不认识任何具体业务类型，所有能力都来自 JobCatalog 注入。
/// </summary>
public sealed class JobEngine
{
    public async Task<JobReport> RunAsync(
        JobDefinition definition,
        JobContext context,
        JobCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();

        var source = catalog.FindSource(definition.InputSource);
        if (source is null)
        {
            messages.Add($"找不到输入源: {definition.InputSource ?? "<default>"}");
            return new JobReport(context.RunId, false, false, startedAt, stopwatch.Elapsed, 0, 0, [], messages);
        }

        if (!Directory.Exists(definition.OutputDirectory))
        {
            Directory.CreateDirectory(definition.OutputDirectory);
        }

        var steps = new List<IPipelineStep>();
        foreach (var name in definition.Steps)
        {
            var step = catalog.FindStep(name);
            if (step is null)
            {
                messages.Add($"找不到处理步骤: {name}，已跳过");
                context.LogWarn($"找不到处理步骤: {name}");
                continue;
            }

            steps.Add(step);
        }

        var sinks = new List<IOutputSink>();
        foreach (var name in definition.Sinks)
        {
            var sink = catalog.FindSink(name);
            if (sink is null)
            {
                messages.Add($"找不到输出目标: {name}，已跳过");
                context.LogWarn($"找不到输出目标: {name}");
                continue;
            }

            sinks.Add(sink);
        }

        if (sinks.Count == 0)
        {
            messages.Add("没有任何可用输出目标，任务不会产生结果");
        }

        WarnOnPathMismatch(definition, context);

        IReadOnlyList<InputItem> items;
        try
        {
            context.Observer.ReportProgress(new JobProgress(0, 0, "收集输入"));
            items = await source.CollectAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Canceled(context, startedAt, stopwatch, messages);
        }
        catch (Exception ex)
        {
            context.LogError($"收集输入失败: {ex.Message}");
            messages.Add($"收集输入失败: {ex.Message}");
            return new JobReport(context.RunId, false, false, startedAt, stopwatch.Elapsed, 0, 0, [], messages);
        }

        messages.Add($"输入 {items.Count} 项（来源 {source.Name}）");
        context.Observer.ReportProgress(new JobProgress(0, items.Count, "处理中"));

        var processed = new List<ProcessedItem>(items.Count);
        var failed = 0;

        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Canceled(context, startedAt, stopwatch, messages);
            }

            var carrier = new ProcessedItem(item);
            foreach (var step in steps)
            {
                try
                {
                    var outcome = await step.ExecuteAsync(carrier, context, cancellationToken).ConfigureAwait(false);
                    if (!outcome.Ok)
                    {
                        carrier.Failed = true;
                        carrier.Error = $"{step.Name}: {outcome.Error}";
                        context.LogWarn($"{item.RelativePath} → {step.Name} 失败: {outcome.Error}");
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(outcome.Note))
                    {
                        carrier.Note(outcome.Note);
                    }
                }
                catch (OperationCanceledException)
                {
                    return Canceled(context, startedAt, stopwatch, messages);
                }
                catch (Exception ex)
                {
                    carrier.Failed = true;
                    carrier.Error = $"{step.Name}: {ex.Message}";
                    context.LogError($"{item.RelativePath} → {step.Name} 异常: {ex.Message}");
                    break;
                }
            }

            if (carrier.Failed)
            {
                failed++;
                if (!definition.ContinueOnItemError)
                {
                    messages.Add($"遇到错误即停止: {item.RelativePath}");
                    break;
                }
            }

            processed.Add(carrier);
            context.Observer.ReportProgress(new JobProgress(processed.Count, items.Count, "处理中"));
        }

        var artifacts = new List<Artifact>();
        foreach (var sink in sinks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Canceled(context, startedAt, stopwatch, messages);
            }

            try
            {
                var written = await sink.WriteAsync(context, processed, cancellationToken).ConfigureAwait(false);
                artifacts.AddRange(written);
                foreach (var artifact in written)
                {
                    messages.Add($"输出 {artifact.Kind}: {Path.GetFileName(artifact.Path)}（{artifact.Bytes} 字节）");
                }
            }
            catch (OperationCanceledException)
            {
                return Canceled(context, startedAt, stopwatch, messages);
            }
            catch (Exception ex)
            {
                messages.Add($"输出 {sink.Name} 失败: {ex.Message}");
                context.LogError($"输出 {sink.Name} 失败: {ex.Message}");
            }
        }

        stopwatch.Stop();
        context.Observer.ReportProgress(new JobProgress(processed.Count, Math.Max(items.Count, 1), "完成"));
        messages.Add($"完成：成功 {processed.Count - failed}，失败 {failed}，耗时 {stopwatch.Elapsed.TotalSeconds:F1}s");

        return new JobReport(
            context.RunId,
            Succeeded: failed == 0,
            Canceled: false,
            startedAt,
            stopwatch.Elapsed,
            processed.Count,
            failed,
            artifacts,
            messages);
    }

    /// <summary>
    /// JobDefinition 里的路径只用于持久化与界面回显，运行时一律以 JobContext 为准。
    /// 两者不一致时给出警告，而不是悄悄按其中一个跑，避免"报表跑的是别的目录"这类事故。
    /// </summary>
    private static void WarnOnPathMismatch(JobDefinition definition, JobContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(definition.InputPath))
            {
                return;
            }

            var declared = Path.GetFullPath(definition.InputPath);
            var effective = Path.GetFullPath(context.InputRoot);
            if (!string.Equals(declared, effective, StringComparison.OrdinalIgnoreCase))
            {
                context.LogWarn($"定义路径 {declared} 与运行路径 {effective} 不一致，以运行路径为准");
            }
        }
        catch (Exception)
        {
            // 路径非法时不阻塞任务，交给输入源报错。
        }
    }

    private static JobReport Canceled(
        JobContext context,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        List<string> messages)
    {
        stopwatch.Stop();
        messages.Add("任务已取消");
        context.LogWarn("任务已取消");
        return new JobReport(context.RunId, false, true, startedAt, stopwatch.Elapsed, 0, 0, [], messages);
    }
}