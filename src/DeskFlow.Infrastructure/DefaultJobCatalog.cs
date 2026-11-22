using DeskFlow.Core.Runtime;
using DeskFlow.Infrastructure.Inputs;
using DeskFlow.Infrastructure.Sinks;
using DeskFlow.Infrastructure.Steps;

namespace DeskFlow.Infrastructure;

/// <summary>
/// 组合根（Composition Root）。新增一种能力 = 在这里 AddSource/AddStep/AddSink 一行，
/// UI 与命令行会自动看到它，不需要改引擎。
/// </summary>
public static class DefaultJobCatalog
{
    public static JobCatalog Build() =>
        new JobCatalog()
            .AddSource(new FileSystemInputSource())
            .AddStep(new FileHashStep())
            .AddStep(new NormalizeNameStep())
            .AddStep(new TextProbeStep())
            .AddSink(new ReportSink());
}
