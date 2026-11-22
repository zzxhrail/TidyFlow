using System.IO;
using System.Globalization;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Observers;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App.Cli;

/// <summary>
/// 命令行入口。参数设计成"能写进 .bat 的短形式"，方便挂 Windows 计划任务。
/// 退出码：0 全部成功；1 有失败条目；2 取消或参数错误。
/// </summary>
public static class CliHost
{
    public static bool IsCliInvocation(string[] args) =>
        args.Any(a => a is "--run" or "-r" or "--help" or "-h" or "--steps");

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