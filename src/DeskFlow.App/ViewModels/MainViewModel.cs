using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Observers;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly JobCatalog _catalog = DefaultJobCatalog.Build();
    private readonly JsonSettingsStore _settings = new();
    private readonly object _logGate = new();

    private CancellationTokenSource? _cts;
    private string _inputPath = string.Empty;
    private string _outputDirectory = AppPaths.DefaultOutputDirectory;
    private bool _recurse = true;
    private bool _isRunning;
    private double _progress;
    private string _statusText = "就绪";
    private string _summary = string.Empty;

    public MainViewModel()
    {
        _inputPath = _settings.Get("input.path", string.Empty) ?? string.Empty;
        _outputDirectory = _settings.Get("output.path", AppPaths.DefaultOutputDirectory) ?? AppPaths.DefaultOutputDirectory;
        _recurse = _settings.Get("input.recurse", true);

        foreach (var step in _catalog.Steps)
        {
            StepOptions.Add(new StepOption(step.Name, step.Description));
        }

        RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => Directory.Exists(OutputDirectory));
    }

    public ObservableCollection<string> Logs { get; } = new();

    public ObservableCollection<StepOption> StepOptions { get; } = new();

    public AsyncRelayCommand RunCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    public string InputPath
    {
        get => _inputPath;
        set => SetProperty(ref _inputPath, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    public bool Recurse
    {
        get => _recurse;
        set => SetProperty(ref _recurse, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsIdle => !IsRunning;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    private async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InputPath) || (!Directory.Exists(InputPath) && !File.Exists(InputPath)))
        {
            Append("ERROR", "请输入存在的输入路径");
            return;
        }

        var steps = StepOptions.Where(static s => s.IsEnabled).Select(static s => s.Name).ToList();
        if (steps.Count == 0)
        {
            Append("WARN", "没有勾选任何处理步骤，只做扫描与报表");
        }

        Logs.Clear();
        Progress = 0;
        Summary = string.Empty;
        IsRunning = true;
        StatusText = "准备中…";

        _settings.Set("input.path", InputPath);
        _settings.Set("output.path", OutputDirectory);
        _settings.Set("input.recurse", Recurse);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileLogPath = Path.Combine(AppPaths.LogDirectory, $"gui-{runId}.log");

        using var fileObserver = TextWriterObserver.ForFile(fileLogPath);
        var observer = new CompositeObserver(
            new DelegateObserver(
                (level, message) => OnUiThread(() => Append(level.ToString().ToUpperInvariant(), message)),
                progress => OnUiThread(() =>
                {
                    Progress = progress.Percent;
                    StatusText = $"{progress.Phase} {progress.Completed}/{progress.Total}";
                })),
            fileObserver);

        var context = new JobContext
        {
            RunId = runId,
            InputRoot = InputPath,
            OutputDirectory = OutputDirectory,
            Observer = observer,
            Settings = _settings,
            Recurse = Recurse,
        };

        var definition = new JobDefinition
        {
            InputPath = InputPath,
            OutputDirectory = OutputDirectory,
            Steps = steps,
            Sinks = _catalog.Sinks.Select(static s => s.Name).ToList(),
            Recurse = Recurse,
        };

        try
        {
            var engine = new JobEngine();
            var report = await Task.Run(() => engine.RunAsync(definition, context, _catalog, token), token);

            Progress = 100;
            StatusText = report.Canceled ? "已取消" : "完成";
            Summary = $"条目 {report.ItemCount} · 失败 {report.FailedCount} · 耗时 {report.Duration.TotalSeconds:F1}s · 输出 {OutputDirectory}";
            foreach (var artifact in report.Artifacts)
            {
                Append("INFO", $"产物: {artifact.Path}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            Append("WARN", "任务已取消");
        }
        catch (Exception ex)
        {
            StatusText = "失败";
            Append("ERROR", ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "正在取消…";
    }

    private void OpenOutput()
    {
        if (!Directory.Exists(OutputDirectory))
        {
            Directory.CreateDirectory(OutputDirectory);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputDirectory}\"") { UseShellExecute = true });
    }

    private void Append(string level, string message)
    {
        lock (_logGate)
        {
            Logs.Add($"{DateTime.Now:HH:mm:ss} [{level,-5}] {message}");
            while (Logs.Count > 2000)
            {
                Logs.RemoveAt(0);
            }
        }
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
