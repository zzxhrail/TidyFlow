using System.Text;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Inputs;
using DeskFlow.Infrastructure.Observers;
using DeskFlow.Infrastructure.Settings;
using DeskFlow.Infrastructure.Steps;

// 零依赖检查程序：不引入 xUnit/NUnit，保证离线可跑、结果就是退出码。
// 用法: dotnet run --project tests/DeskFlow.Checks

var failures = 0;
var checks = 0;

void Check(string name, bool condition, string? detail = null)
{
    checks++;
    if (condition)
    {
        Console.WriteLine($"  PASS  {name}");
        return;
    }

    failures++;
    Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " -> " + detail)}");
}

string NewTempDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "deskflow-checks", Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
}

Console.WriteLine("DeskFlow 内置检查");
Console.WriteLine();

// ---------- 1. 文件名规范化 ----------
Console.WriteLine("[1] NormalizeNameStep");
{
    var step = new NormalizeNameStep();
    var root = NewTempDir();
    var ctx = TestContext(root);

    var fullWidth = Path.Combine(root, "　报告　ＡＢＣ.txt");
    var item = new ProcessedItem(new InputItem(fullWidth, "报告 ABC.txt", 0, DateTimeOffset.UtcNow));
    await step.ExecuteAsync(item, ctx, default);
    Check("全角转半角 + 去首尾空格", item.Get("name.suggested") == "报告 ABC.txt", item.Get("name.suggested"));

    var illegal = new ProcessedItem(new InputItem(Path.Combine(root, "a*b?c.md"), "a*b?c.md", 0, DateTimeOffset.UtcNow));
    await step.ExecuteAsync(illegal, ctx, default);
    Check("非法字符被替换", illegal.Get("name.suggested") == "a_b_c.md", illegal.Get("name.suggested"));

    var reserved = new ProcessedItem(new InputItem(Path.Combine(root, "con.txt"), "con.txt", 0, DateTimeOffset.UtcNow));
    await step.ExecuteAsync(reserved, ctx, default);
    Check("保留名被改写", reserved.Get("name.suggested") == "_con.txt", reserved.Get("name.suggested"));
}

// ---------- 2. 文本探测 ----------
Console.WriteLine("[2] TextProbeStep");
{
    var step = new TextProbeStep();
    var root = NewTempDir();
    var ctx = TestContext(root);

    var utf8Path = Path.Combine(root, "a.txt");
    await File.WriteAllTextAsync(utf8Path, "第一行\n第二行\n第三行", new UTF8Encoding(false));
    var utf8Item = new ProcessedItem(new InputItem(utf8Path, "a.txt", new FileInfo(utf8Path).Length, DateTimeOffset.UtcNow));
    await step.ExecuteAsync(utf8Item, ctx, default);
    Check("UTF-8 行数=3", utf8Item.Get("text.lines") == "3", utf8Item.Get("text.lines"));
    Check("UTF-8 编码识别", utf8Item.Get("text.encoding") == "utf-8", utf8Item.Get("text.encoding"));

    var bomPath = Path.Combine(root, "b.csv");
    await File.WriteAllTextAsync(bomPath, "x,y\n1,2", new UTF8Encoding(true));
    var bomItem = new ProcessedItem(new InputItem(bomPath, "b.csv", new FileInfo(bomPath).Length, DateTimeOffset.UtcNow));
    await step.ExecuteAsync(bomItem, ctx, default);
    Check("BOM 被剥离且不污染首行", bomItem.Get("text.chars") == "7", bomItem.Get("text.chars"));

    var binPath = Path.Combine(root, "c.exe");
    await File.WriteAllBytesAsync(binPath, [0x4D, 0x5A, 0x00, 0x01]);
    var binItem = new ProcessedItem(new InputItem(binPath, "c.exe", 4, DateTimeOffset.UtcNow));
    await step.ExecuteAsync(binItem, ctx, default);
    Check("二进制文件跳过且不算失败", binItem.Get("text.kind") == "binary" && !binItem.Failed);
}

// ---------- 3. 指纹 ----------
Console.WriteLine("[3] FileHashStep");
{
    var step = new FileHashStep();
    var root = NewTempDir();
    var ctx = TestContext(root);
    var a = Path.Combine(root, "same1.txt");
    var b = Path.Combine(root, "same2.txt");
    await File.WriteAllTextAsync(a, "hello deskflow");
    await File.WriteAllTextAsync(b, "hello deskflow");

    var ia = new ProcessedItem(new InputItem(a, "same1.txt", new FileInfo(a).Length, DateTimeOffset.UtcNow));
    var ib = new ProcessedItem(new InputItem(b, "same2.txt", new FileInfo(b).Length, DateTimeOffset.UtcNow));
    await step.ExecuteAsync(ia, ctx, default);
    await step.ExecuteAsync(ib, ctx, default);

    Check("同内容同指纹", ia.Get("hash.sha256") == ib.Get("hash.sha256"), ia.Get("hash.sha256"));
    Check("指纹为 64 位小写十六进制",
        ia.Get("hash.sha256") is { Length: 64 } h && h.All(c => "0123456789abcdef".Contains(c)));
    Check("模式标记 full", ia.Get("hash.mode") == "full");
}

// ---------- 4. 输入源 ----------
Console.WriteLine("[4] FileSystemInputSource");
{
    var root = NewTempDir();
    Directory.CreateDirectory(Path.Combine(root, "sub", "deep"));
    await File.WriteAllTextAsync(Path.Combine(root, "top.txt"), "a");
    await File.WriteAllTextAsync(Path.Combine(root, "sub", "mid.txt"), "b");
    await File.WriteAllTextAsync(Path.Combine(root, "sub", "deep", "leaf.txt"), "c");

    var source = new FileSystemInputSource();
    var recursive = await source.CollectAsync(TestContext(root, recurse: true), default);
    var flat = await source.CollectAsync(TestContext(root, recurse: false), default);

    Check("递归收集 3 个文件", recursive.Count == 3, recursive.Count.ToString());
    Check("不递归只收集 1 个", flat.Count == 1, flat.Count.ToString());
    Check("结果按相对路径排序", recursive.Select(i => i.RelativePath).SequenceEqual(recursive.Select(i => i.RelativePath).OrderBy(x => x, StringComparer.Ordinal)));
}

// ---------- 5. 端到端 ----------
Console.WriteLine("[5] JobEngine 端到端");
{
    var root = NewTempDir();
    var outDir = Path.Combine(root, "_out");
    await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "第一行\n第二行");
    await File.WriteAllTextAsync(Path.Combine(root, "two.md"), "# 标题");
    Directory.CreateDirectory(Path.Combine(root, "nested"));
    await File.WriteAllTextAsync(Path.Combine(root, "nested", "three.json"), "{\"k\":1}");

    var catalog = DefaultJobCatalog.Build();
    var definition = new JobDefinition
    {
        InputPath = root,
        OutputDirectory = outDir,
        Steps = ["hash", "normalize-name", "text-probe"],
        Sinks = ["report"],
        Recurse = true,
    };

    var context = TestContext(root, output: outDir);
    var report = await new JobEngine().RunAsync(definition, context, catalog);

    Check("任务成功", report.Succeeded, string.Join(" | ", report.Messages));
    Check("处理 3 条", report.ItemCount == 3, report.ItemCount.ToString());
    Check("无失败条目", report.FailedCount == 0);
    Check("产出 2 个文件", report.Artifacts.Count == 2, report.Artifacts.Count.ToString());
    Check("report.csv 存在", File.Exists(Path.Combine(outDir, "report.csv")));
    Check("report.json 存在", File.Exists(Path.Combine(outDir, "report.json")));

    var csv = await File.ReadAllTextAsync(Path.Combine(outDir, "report.csv"));
    Check("CSV 含动态列 hash.sha256", csv.Contains("hash.sha256"));
    Check("CSV 行数=表头+3", csv.TrimEnd().Split('\n').Length == 4, csv.TrimEnd().Split('\n').Length.ToString());

    var head = await File.ReadAllBytesAsync(Path.Combine(outDir, "report.csv"));
    Check("CSV 带 UTF-8 BOM（Excel 不乱码）", head.Length > 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF);

    // 取消必须能立刻返回
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var canceled = await new JobEngine().RunAsync(definition, TestContext(root, output: outDir), catalog, cts.Token);
    Check("取消后返回 Canceled 标记", canceled.Canceled && !canceled.Succeeded);
}

// ---------- 6. 输出目标缺失 ----------
Console.WriteLine("[6] 契约边界");
{
    var root = NewTempDir();
    var catalog = DefaultJobCatalog.Build();
    var definition = new JobDefinition
    {
        InputPath = Path.Combine(root, "not-exists"),
        OutputDirectory = Path.Combine(root, "_out"),
        Steps = ["hash"],
        Sinks = ["report"],
    };

    var missingContext = TestContext(Path.Combine(root, "not-exists"), output: Path.Combine(root, "_out"));
    var report = await new JobEngine().RunAsync(definition, missingContext, catalog);
    Check("输入不存在时任务失败且带消息", !report.Succeeded && report.Messages.Count > 0);

    var emptyDir = NewTempDir();
    var emptyOut = Path.Combine(emptyDir, "_out");
    var emptyDef = new JobDefinition
    {
        InputPath = emptyDir,
        OutputDirectory = emptyOut,
        Steps = ["hash", "normalize-name", "text-probe"],
        Sinks = ["report"],
    };
    var emptyReport = await new JobEngine().RunAsync(emptyDef, TestContext(emptyDir, output: emptyOut), catalog);
    var headerOnly = (await File.ReadAllTextAsync(Path.Combine(emptyOut, "report.csv"))).TrimEnd().Split('\n');
    Check("空目录：任务成功且只有表头", emptyReport.Succeeded && headerOnly.Length == 1, $"lines={headerOnly.Length}");
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"全部通过：{checks} 项检查"
    : $"失败 {failures} / {checks} 项");
return failures == 0 ? 0 : 1;

static JobContext TestContext(string inputRoot, bool recurse = true, string? output = null) => new()
{
    RunId = "check",
    InputRoot = inputRoot,
    OutputDirectory = output ?? Path.Combine(inputRoot, "_out"),
    Observer = NullJobObserver.Instance,
    Settings = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "deskflow-checks", "settings.json")),
    Recurse = recurse,
};