using System.Text;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Categorization;
using DeskFlow.Core.Models;
using DeskFlow.Core.Shell;
using DeskFlow.Core.Runtime;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Archive;
using DeskFlow.Infrastructure.Index;
using DeskFlow.Infrastructure.Inputs;
using DeskFlow.Infrastructure.Observers;
using DeskFlow.Infrastructure.Settings;
using DeskFlow.Infrastructure.Steps;
using DeskFlow.Infrastructure.Watch;

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


// ---------- 7. 分类目录 ----------
Console.WriteLine("[7] CategoryCatalog");
{
    var catalog = new CategoryCatalog();
    Check("docx → 办公文档", catalog.Resolve("季度报告.docx").Name == "办公文档", catalog.Resolve("季度报告.docx").Name);
    Check("cs → 代码", catalog.Resolve("Program.cs").Name == "代码", catalog.Resolve("Program.cs").Name);
    Check("未知扩展名 → 其他", catalog.Resolve("a.xyz").Name == "其他", catalog.Resolve("a.xyz").Name);
    Check("无扩展名 → 其他", catalog.Resolve("README").Name == "其他");

    catalog.UpsertUserCategory("日志", [".log", "trace"]);
    Check("自定义类别生效", catalog.Resolve("app.log").Name == "日志", catalog.Resolve("app.log").Name);
    Check("扩展名可省略点号", catalog.Resolve("x.trace").Name == "日志", catalog.Resolve("x.trace").Name);

    catalog.UpsertUserCategory("办公文档", [".pages"]);
    Check("同名覆盖内置且保留原扩展名",
        catalog.Resolve("a.pages").Name == "办公文档" && catalog.Resolve("a.docx").Name == "办公文档");
    Check("用户类别可删除", catalog.RemoveUserCategory("日志") && catalog.Resolve("app.log").Name == "其他");
}

// ---------- 8. 归档器 ----------
Console.WriteLine("[8] FileArchiver");
{
    var root = NewTempDir();
    var inbox = Path.Combine(root, "inbox");
    var archiveRoot = Path.Combine(root, "archive");
    Directory.CreateDirectory(inbox);

    var catalog = new CategoryCatalog();
    var archiver = new FileArchiver(catalog);

    var doc = Path.Combine(inbox, "季度报告.docx");
    await File.WriteAllTextAsync(doc, "v1");
    var record = NewRecord(doc, FileSource.WeChat);
    var outcome = await archiver.ArchiveAsync(record, new ArchiveOptions { ArchiveRoot = archiveRoot });

    Check("归档成功", outcome.Success && !outcome.Duplicate, outcome.Error);
    Check("落到类别子目录", record.ArchivedPath == Path.Combine(archiveRoot, "办公文档", "季度报告.docx"), record.ArchivedPath);
    Check("类别被写回记录", record.Category == "办公文档");
    Check("复制模式保留原件", File.Exists(doc));

    var duplicate = await archiver.ArchiveAsync(NewRecord(doc, FileSource.WeChat), new ArchiveOptions { ArchiveRoot = archiveRoot });
    Check("同内容判重", duplicate.Duplicate && duplicate.Record.ArchivedPath == record.ArchivedPath);

    var conflict = Path.Combine(inbox, "季度报告.docx");
    await File.WriteAllTextAsync(conflict, "v2-different-content");
    var second = NewRecord(conflict, FileSource.Qq);
    var conflictOutcome = await archiver.ArchiveAsync(second, new ArchiveOptions { ArchiveRoot = archiveRoot });
    Check("同名不同内容改名 (2)", conflictOutcome.Success && second.ArchivedPath!.EndsWith("季度报告 (2).docx", StringComparison.Ordinal), second.ArchivedPath);

    var moveSource = Path.Combine(inbox, "脚本.py");
    await File.WriteAllTextAsync(moveSource, "print(1)");
    var moveRecord = NewRecord(moveSource, FileSource.DingTalk);
    await archiver.ArchiveAsync(moveRecord, new ArchiveOptions { ArchiveRoot = archiveRoot, MoveInsteadOfCopy = true });
    Check("移动模式删除原件", !File.Exists(moveSource) && File.Exists(moveRecord.ArchivedPath));
    Check("py 归到代码目录", moveRecord.ArchivedPath!.Contains(Path.Combine("archive", "代码")), moveRecord.ArchivedPath);

    // 原件消失后必须报失败，而不是假装成功
    var ghost = NewRecord(Path.Combine(inbox, "不存在.pdf"), FileSource.Qq);
    var ghostOutcome = await archiver.ArchiveAsync(ghost, new ArchiveOptions { ArchiveRoot = archiveRoot });
    Check("源文件不存在 → 失败", !ghostOutcome.Success && ghost.Status == ArchiveStatus.Failed);
}

// ---------- 9. 监听器 ----------
Console.WriteLine("[9] IncomingWatcher");
{
    var dir = NewTempDir();
    await File.WriteAllTextAsync(Path.Combine(dir, "old.txt"), "already here");
    var watcher = new IncomingWatcher([new WatchTarget("测试", dir, FileSource.Qq, true)]);
    watcher.PrimeExisting();
    var first = await Task.FromResult(watcher.ScanOnce());
    Check("启动前的旧文件不重复上报", first.Count == 0, first.Count.ToString());

    var file = Path.Combine(dir, "new.txt");
    await File.WriteAllTextAsync(file, "abc");
    var probe = await Task.FromResult(watcher.ScanOnce());
    await File.AppendAllTextAsync(file, "在写");
    var writing = await Task.FromResult(watcher.ScanOnce());
    var stable = await Task.FromResult(watcher.ScanOnce());

    Check("首次发现只做大小探针", probe.Count == 0, probe.Count.ToString());
    Check("仍在写入的文件不上报", writing.Count == 0, writing.Count.ToString());
    Check("大小稳定后上报一次", stable.Count == 1 && stable[0].FullPath == file, stable.Count.ToString());
    Check("同一文件不会重复上报", watcher.ScanOnce().Count == 0);
    Check("来源被正确标注", stable.Count == 1 && stable[0].Source == FileSource.Qq);
    watcher.Dispose();
}

// ---------- 10. 索引 ----------
Console.WriteLine("[10] JsonArchiveIndex");
{
    var path = Path.Combine(NewTempDir(), "index.json");
    var index = new JsonArchiveIndex(path);
    var archived = NewRecord(Path.Combine(NewTempDir(), "a.docx"), FileSource.Qq);
    archived.Status = ArchiveStatus.Archived;
    archived.ArchivedAt = DateTimeOffset.Now;
    index.Upsert(archived);

    var pending = NewRecord(Path.Combine(NewTempDir(), "b.pdf"), FileSource.WeChat);
    pending.Status = ArchiveStatus.Pending;
    index.Upsert(pending);

    Check("归档记录进 Recent", index.Recent(10).Any(r => r.Id == archived.Id));
    Check("待接收单独成列", index.Pending().Count == 1 && index.Pending()[0].Id == pending.Id);
    Check("按源路径判重", index.KnowsSourcePath(archived.SourcePath));

    var reloaded = new JsonArchiveIndex(path);
    Check("索引持久化", reloaded.All.Count == 2 && reloaded.Pending().Count == 1, reloaded.All.Count.ToString());

    reloaded.Remove(pending.Id);
    Check("删除记录", new JsonArchiveIndex(path).All.Count == 1);
}

// ---------- 11. 配置 ----------
Console.WriteLine("[11] ArchiveSettings");
{
    var proxy = new FakeSettingsProxy();
    var settings = new ArchiveSettings(proxy);
    settings.SetAutoDownload(FileSource.DingTalk, true);
    Check("自动下载开关落盘", settings.IsAutoDownload(FileSource.DingTalk) && !settings.IsAutoDownload(FileSource.Qq));

    settings.AddUserCategory("合同", [".pdf2"]);
    var reloaded = new ArchiveSettings(proxy);
    Check("自定义类别持久化", reloaded.UserCategories.Any(c => c[0] == "合同"), reloaded.UserCategories.Count.ToString());

    settings.Shortcuts.Add(new DeskFlow.Infrastructure.Settings.ShortcutItem { Name = "计算器", Path = @"C:\Windows\System32\calc.exe" });
    settings.Save();
    Check("快捷方式持久化", new ArchiveSettings(proxy).Shortcuts.Any(s => s.Name == "计算器"));
}


// ---------- 12. 面板命中（拖动/缩放/顶住） ----------
Console.WriteLine("[12] PanelHitTest");
{
    const double w = 376, h = 640;
    Check("标题栏中部 → 可拖动", PanelHitTest.Hit(100, 30, w, h, pinned: false) == PanelHitArea.Caption);
    Check("顶住后标题栏不再可拖", PanelHitTest.Hit(100, 30, w, h, pinned: true) == PanelHitArea.None);
    Check("右侧按钮区留给控件", PanelHitTest.Hit(300, 30, w, h, pinned: false) == PanelHitArea.None,
        PanelHitTest.Hit(300, 30, w, h, false).ToString());
    Check("左上角 → 缩放", PanelHitTest.Hit(4, 4, w, h, false) == PanelHitArea.TopLeft);
    Check("右下角 → 缩放", PanelHitTest.Hit(w - 4, h - 4, w, h, false) == PanelHitArea.BottomRight);
    Check("右边中点 → 横向缩放", PanelHitTest.Hit(w - 4, 300, w, h, false) == PanelHitArea.Right);
    Check("正文区域不拦截点击", PanelHitTest.Hit(200, 300, w, h, false) == PanelHitArea.None);
    Check("顶住仍能拉边角", PanelHitTest.Hit(w - 4, h - 4, w, h, pinned: true) == PanelHitArea.BottomRight);
    Check("零尺寸不崩", PanelHitTest.Hit(10, 10, 0, 0, false) == PanelHitArea.None);
}


// ---------- 13. 目录探测（重定向文档目录 / 客户端各版本布局） ----------
Console.WriteLine("[13] SourceDirectoryResolver");
{
    var profile = NewTempDir();
    var docs = Path.Combine(profile, "docu");          // 模拟"文档"被重定向到别的盘
    Directory.CreateDirectory(docs);

    // QQNT 9.x
    var qqData = Path.Combine(docs, "Tencent Files", "123456", "nt_qq", "nt_data");
    var qqFile = Path.Combine(qqData, "File");
    Directory.CreateDirectory(Path.Combine(qqFile, "Thumb"));
    // 微信 4.x
    var wx4 = Path.Combine(docs, "xwechat_files", "wxid_abc_1234", "msg", "file", "2026-11");
    Directory.CreateDirectory(wx4);
    // 微信 3.x
    var wx3 = Path.Combine(docs, "WeChat Files", "wxid_old", "FileStorage", "File");
    Directory.CreateDirectory(wx3);
    // 钉钉
    var dt = Path.Combine(docs, "DingTalk", "rail_123", "file");
    Directory.CreateDirectory(dt);
    Directory.CreateDirectory(Path.Combine(profile, "Downloads"));

    var appData = Path.Combine(profile, "appdata");
    Directory.CreateDirectory(appData);
    var targets = SourceDirectoryResolver.Discover(userProfile: profile, documentsRoot: docs, appDataRoot: appData);
    var qq = targets.FirstOrDefault(t => t.Source == FileSource.Qq);
    Check("重定向文档目录下的 QQNT 被识别（盯 nt_data 整个子树）", qq is not null && qq.Directory == qqData, qq?.Directory);
    Check("微信 4.x 被识别", targets.Any(t => t.Source == FileSource.WeChat && t.Directory.EndsWith(Path.Combine("msg", "file"), StringComparison.Ordinal)));
    Check("微信 3.x 被识别", targets.Any(t => t.Source == FileSource.WeChat && t.Directory.EndsWith(Path.Combine("FileStorage", "File"), StringComparison.Ordinal)));
    Check("钉钉被识别", targets.Any(t => t.Source == FileSource.DingTalk && t.Directory == dt));
    Check("下载目录被识别", targets.Any(t => t.Source == FileSource.Downloads));
    Check("同一目录不重复", targets.Select(t => t.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count() == targets.Count);

    // 未安装/未收过文件：给出预测路径且 IsDetected=false，界面显示"等待首次接收"
    var emptyProfile = NewTempDir();
    var emptyDocs = Path.Combine(emptyProfile, "docu");
    Directory.CreateDirectory(emptyDocs);
    Directory.CreateDirectory(Path.Combine(emptyDocs, "DingTalk"));
    var emptyAppData = Path.Combine(emptyProfile, "appdata");
    Directory.CreateDirectory(Path.Combine(emptyAppData, "DingTalk"));
    var predicted = SourceDirectoryResolver.Discover(userProfile: emptyProfile, documentsRoot: emptyDocs, appDataRoot: emptyAppData);
    var dtPredicted = predicted.FirstOrDefault(t => t.Source == FileSource.DingTalk);
    Check("钉钉未收过文件时给预测路径", dtPredicted is not null && !dtPredicted.IsDetected, dtPredicted?.Directory);

    // QQ 缓存目录（Thumb/Emoji/log）不算收到的文件
    var thumb = Path.Combine(qqFile, "Thumb", "abc.jpg");
    var realFile = Path.Combine(qqFile, "Ori", "报告.docx");
    var datalineFile = Path.Combine(qqData, "dataline", "手机传的.pdf");
    Check("Thumb 被判定为噪声", IncomingWatcher.IsNoise(thumb, qqData));
    Check("File 下 Ori 子目录的文件不算噪声", !IncomingWatcher.IsNoise(realFile, qqData));
    Check("dataline 里的文件不算噪声", !IncomingWatcher.IsNoise(datalineFile, qqData));
    Check("日志目录被判定为噪声", IncomingWatcher.IsNoise(Path.Combine(qqData, "log", "a.log"), qqData));
    Check("Emoji 目录被判定为噪声", IncomingWatcher.IsNoise(Path.Combine(qqData, "Emoji", "x.gif"), qqData));
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"全部通过：{checks} 项检查"
    : $"失败 {failures} / {checks} 项");
return failures == 0 ? 0 : 1;

static FileRecord NewRecord(string path, FileSource source) => new()
{
    Id = Guid.NewGuid().ToString("N"),
    SourcePath = path,
    FileName = Path.GetFileName(path),
    SizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
    Source = source,
    Category = "其他",
};

static JobContext TestContext(string inputRoot, bool recurse = true, string? output = null) => new()
{
    RunId = "check",
    InputRoot = inputRoot,
    OutputDirectory = output ?? Path.Combine(inputRoot, "_out"),
    Observer = NullJobObserver.Instance,
    Settings = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "deskflow-checks", "settings.json")),
    Recurse = recurse,
};
/// <summary>内存版设置代理，自检不碰真实 %LOCALAPPDATA%\DeskFlow\settings.json。</summary>
internal sealed class FakeSettingsProxy : DeskFlow.Infrastructure.Settings.ISettingsProxy
{
    private readonly Dictionary<string, object?> _values = new();

    public T? Get<T>(string key, T? fallback = default) =>
        _values.TryGetValue(key, out var value) && value is T typed ? typed : fallback;

    public void Set<T>(string key, T value) => _values[key] = value;

    public void Flush()
    {
    }
}