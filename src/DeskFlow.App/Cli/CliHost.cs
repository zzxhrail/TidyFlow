using System.IO;
using System.Globalization;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Observers;
using DeskFlow.Infrastructure.Settings;
using DeskFlow.Infrastructure.Watch;

namespace DeskFlow.App.Cli;

/// <summary>
/// 命令行入口。参数设计成"能写进 .bat 的短形式"，方便挂 Windows 计划任务。
/// 退出码：0 全部成功；1 有失败条目；2 取消或参数错误。
/// </summary>
public static class CliHost
{
    public static bool IsCliInvocation(string[] args) =>
        args.Any(a => a is "--run" or "-r" or "--help" or "-h" or "--steps" or "--diagnose" or "--selftest");

    public static int Run(string[] args)
    {
        ConsoleAttach.TryAttach();
        try
        {
            return RunCore(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fatal] {ex.Message}");
            return 2;
        }
    }

    private static int RunCore(string[] args)
    {
        var options = Parse(args);

        if (args.Any(static a => a is "--diagnose" or "--selftest"))
        {
            return RunDiagnose();
        }

        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.Input))
        {
            Console.Error.WriteLine("[fatal] 缺少 --run <输入路径>");
            PrintHelp();
            return 2;
        }

        var outputDirectory = string.IsNullOrWhiteSpace(options.Output)
            ? AppPaths.DefaultOutputDirectory
            : options.Output;

        var catalog = DefaultJobCatalog.Build();
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logPath = Path.Combine(AppPaths.LogDirectory, $"cli-{runId}.log");

        using var fileObserver = TextWriterObserver.ForFile(logPath);
        var observer = new CompositeObserver(
            new TextWriterObserver(Console.Out),
            fileObserver);

        var settings = new JsonSettingsStore();
        var context = new JobContext
        {
            RunId = runId,
            InputRoot = options.Input!,
            OutputDirectory = outputDirectory,
            Observer = observer,
            Settings = settings,
            Recurse = !options.NoRecurse,
        };

        var steps = options.Steps.Count > 0
            ? options.Steps
            : catalog.Steps.Select(static s => s.Name).ToList();

        var definition = new JobDefinition
        {
            InputPath = options.Input!,
            OutputDirectory = outputDirectory,
            Steps = steps,
            Sinks = catalog.Sinks.Select(static s => s.Name).ToList(),
            ContinueOnItemError = !options.StopOnError,
            Recurse = !options.NoRecurse,
        };

        Console.WriteLine($"DeskFlow CLI  run={runId}");
        Console.WriteLine($"  输入: {definition.InputPath}");
        Console.WriteLine($"  输出: {outputDirectory}");
        Console.WriteLine($"  步骤: {string.Join(", ", steps)}");
        Console.WriteLine();

        var report = new JobEngine().RunAsync(definition, context, catalog).GetAwaiter().GetResult();

        Console.WriteLine();
        var verdict = report.Canceled
            ? "已取消"
            : report.Succeeded
                ? "成功"
                : report.ItemCount == 0
                    ? "失败"
                    : "部分失败";
        Console.WriteLine($"结果: {verdict}");
        Console.WriteLine($"  条目: {report.ItemCount}  失败: {report.FailedCount}  耗时: {report.Duration.TotalSeconds:F1}s");
        foreach (var artifact in report.Artifacts)
        {
            Console.WriteLine($"  产物: {artifact.Path}");
        }

        Console.WriteLine($"  日志: {logPath}");
        ConsoleAttach.Detach();
        return report.Succeeded ? 0 : 1;
    }

    /// <summary>
    /// 监听诊断：把每个被监听目录的"存在性 / 最近 24h 非噪声新文件数 / 最新文件名"打出来。
    /// 用来区分"软件没监听到"和"客户端根本没落盘"。
    /// </summary>
    private static int RunDiagnose()
    {
        var settings = new ArchiveSettings();
        var targets = new List<DeskFlow.Core.Models.WatchTarget>();
        targets.AddRange(SourceDirectoryResolver.Discover(cachedRoots: settings.DiscoveryRoots));
        foreach (var extra in settings.ExtraTargets)
        {
            if (Directory.Exists(extra.Path))
            {
                targets.Add(new DeskFlow.Core.Models.WatchTarget(extra.Name, extra.Path, DeskFlow.Core.Models.FileSource.Manual, IsDetected: false));
            }
        }

        Console.WriteLine("DeskFlow 监听诊断");
        Console.WriteLine($"Documents = {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}");
        Console.WriteLine($"监听目录  = {targets.Count} 个");
        Console.WriteLine();

        var probes = WatchDiagnostics.Probe(targets, TimeSpan.FromHours(24));
        foreach (var probe in probes)
        {
            var state = probe.Exists
                ? (probe.RecentCount > 0 ? $"24h 内 {probe.RecentCount} 个文件" : "目录存在，24h 内没有新文件")
                : "目录不存在";
            Console.WriteLine($"[{probe.Source.DisplayName()}] {probe.Name}");
            Console.WriteLine($"    路径: {probe.Path}");
            Console.WriteLine($"    状态: {state}");
            if (probe.LatestName is not null)
            {
                Console.WriteLine($"    最新: {probe.LatestName}  ({probe.LatestTime:yyyy-MM-dd HH:mm})");
            }

            Console.WriteLine();
        }

        var total = probes.Sum(static p => p.RecentCount);
        Console.WriteLine($"合计：24 小时内非噪声新文件 {total} 个");
        Console.WriteLine("如果这里全是 0，说明聊天工具还没把文件写到本地 —— 在客户端里点一下文件让它下载。");
        ConsoleAttach.Detach();
        return 0;
    }

    private sealed record CliOptions(
        string? Input,
        string? Output,
        List<string> Steps,
        bool NoRecurse,
        bool StopOnError,
        bool ShowHelp);

    private static CliOptions Parse(string[] args)
    {
        string? input = null;
        string? output = null;
        var steps = new List<string>();
        var noRecurse = false;
        var stopOnError = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--run" or "-r" or "--input" or "-i":
                    input = Next(args, ref i);
                    break;
                case "--out" or "-o" or "--output":
                    output = Next(args, ref i);
                    break;
                case "--steps" or "-s":
                    var raw = Next(args, ref i);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        steps.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }

                    break;
                case "--no-recurse":
                    noRecurse = true;
                    break;
                case "--stop-on-error":
                    stopOnError = true;
                    break;
                case "--help" or "-h" or "/?":
                    help = true;
                    break;
                default:
                    if (!args[i].StartsWith('-') && input is null)
                    {
                        input = args[i];
                    }

                    break;
            }
        }

        return new CliOptions(input, output, steps, noRecurse, stopOnError, help);
    }

    private static string? Next(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        index++;
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            DeskFlow —— 批量文件盘点与报表工具

            用法:
              DeskFlow.exe --run <输入路径> [--out <输出目录>] [--steps a,b,c] [--no-recurse] [--stop-on-error]

            参数:
              --run, -r, --input, -i   输入目录或单个文件（必填）
              --out, -o, --output      输出目录（默认 文档\DeskFlow\output）
              --steps, -s              逗号分隔的步骤名，默认全部
                                       可选: hash, normalize-name, text-probe
              --no-recurse             不递归子目录
              --stop-on-error          单条失败即停止整批
              --help, -h               显示本帮助

            退出码: 0 成功 / 1 存在失败条目 / 2 参数或运行错误
            """);
    }
}