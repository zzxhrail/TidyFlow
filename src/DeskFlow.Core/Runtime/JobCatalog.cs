using DeskFlow.Core.Abstractions;

namespace DeskFlow.Core.Runtime;

/// <summary>
/// 组件目录（组合根之外唯一的"名字 → 实现"映射）。
/// 新增一种输入 / 步骤 / 输出只需要在这里注册一次，UI 与命令行自动可见。
/// </summary>
public sealed class JobCatalog
{
    private readonly Dictionary<string, IInputSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPipelineStep> _steps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IOutputSink> _sinks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IInputSource> Sources => _sources.Values;

    public IReadOnlyCollection<IPipelineStep> Steps => _steps.Values;

    public IReadOnlyCollection<IOutputSink> Sinks => _sinks.Values;

    public JobCatalog AddSource(IInputSource source)
    {
        _sources[source.Name] = source;
        return this;
    }

    public JobCatalog AddStep(IPipelineStep step)
    {
        _steps[step.Name] = step;
        return this;
    }

    public JobCatalog AddSink(IOutputSink sink)
    {
        _sinks[sink.Name] = sink;
        return this;
    }

    public IInputSource? FindSource(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return _sources.Values.FirstOrDefault();
        }

        return _sources.TryGetValue(name, out var source) ? source : null;
    }

    public IPipelineStep? FindStep(string name) => _steps.TryGetValue(name, out var step) ? step : null;

    public IOutputSink? FindSink(string name) => _sinks.TryGetValue(name, out var sink) ? sink : null;
}
