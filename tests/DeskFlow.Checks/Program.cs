using System.Text;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Categorization;
using DeskFlow.Core.Models;
using DeskFlow.Core.Shell;
using DeskFlow.Core.Runtime;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Archive;
using DeskFlow.Infrastructure.Index;
using DeskFlow.Infrastructure.History;
using DeskFlow.Infrastructure.Scan;
using DeskFlow.Infrastructure.Services;
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


// ---------- 14. 全局监听过滤 ----------
Console.WriteLine("[14] GlobalWatchFilter");
{
    var options = new GlobalWatchOptions
    {
        Enabled = true,
        Roots = [@"D:\watch"],
        Extensions = new HashSet<string>([".docx", ".pdf"], StringComparer.OrdinalIgnoreCase),
        ExcludedPaths = [@"D:\archive"],
    };

    Check("docx 命中", GlobalWatchFilter.ShouldWatch(@"D:\watch\a.docx", options, 100, FileAttributes.Normal));
    Check("未选类型不命中", !GlobalWatchFilter.ShouldWatch(@"D:\watch\a.exe", options, 100, FileAttributes.Normal));
    Check("系统目录不命中", !GlobalWatchFilter.ShouldWatch(@"C:\Windows\a.docx", options, 100, FileAttributes.Normal));
    Check("AppData 不命中", !GlobalWatchFilter.ShouldWatch(@"C:\Users\x\AppData\Roaming\a.docx", options, 100, FileAttributes.Normal));
    Check("归档目录不命中", !GlobalWatchFilter.ShouldWatch(@"D:\archive\a.docx", options, 100, FileAttributes.Normal));
    Check("Word 临时文件不命中", !GlobalWatchFilter.ShouldWatch(@"D:\watch\~$a.docx", options, 100, FileAttributes.Normal));
    Check("0 字节不命中", !GlobalWatchFilter.ShouldWatch(@"D:\watch\a.docx", options, 0, FileAttributes.Normal));
    Check("隐藏文件不命中", !GlobalWatchFilter.ShouldWatch(@"D:\watch\a.docx", options, 100, FileAttributes.Hidden));
    Check("关闭时不命中", !GlobalWatchFilter.ShouldWatch(@"D:\watch\a.docx",
        new GlobalWatchOptions { Enabled = false, Extensions = options.Extensions }, 100, FileAttributes.Normal));
    Check("IsUnder 子树判定", GlobalWatchFilter.IsUnder(@"D:\archive\2026\x.txt", @"D:\archive"));
    Check("IsUnder 不误判同前缀", !GlobalWatchFilter.IsUnder(@"D:\archive2\x.txt", @"D:\archive"));
    Check("默认类型含 docx/pdf", GlobalWatchFilter.DefaultExtensions.Contains(".docx") && GlobalWatchFilter.DefaultExtensions.Contains(".pdf"));
}


// ---------- 15. 撤销归档 + 归档日志 ----------
Console.WriteLine("[15] Undo & ArchiveLog");
{
    var root = NewTempDir();
    var inbox = Path.Combine(root, "inbox");
    var archiveRoot = Path.Combine(root, "archive");
    Directory.CreateDirectory(inbox);
    var archiver = new FileArchiver(new CategoryCatalog());

    var source = Path.Combine(inbox, "合同.docx");
    await File.WriteAllTextAsync(source, "v1");
    var record = NewRecord(source, FileSource.Global);

    await archiver.ArchiveAsync(record, new ArchiveOptions { ArchiveRoot = archiveRoot, MoveInsteadOfCopy = true });
    Check("归档后原文件已被移走", !File.Exists(source) && File.Exists(record.ArchivedPath), record.ArchivedPath);

    var undo = await archiver.UndoAsync(record);
    Check("撤销成功", undo.Success, undo.Error);
    Check("文件回到原位置", File.Exists(source));
    Check("归档目录已清空", record.ArchivedPath is null && !Directory.EnumerateFiles(archiveRoot, "*", SearchOption.AllDirectories).Any());
    Check("状态标记为已撤销", record.Status == ArchiveStatus.Reverted);
    Check("记录里保留了放回路径", record.RestoredPath == source);

    // 原位置被占用时不能覆盖
    var record2 = NewRecord(source, FileSource.Global);
    await archiver.ArchiveAsync(record2, new ArchiveOptions { ArchiveRoot = archiveRoot, MoveInsteadOfCopy = true });
    await File.WriteAllTextAsync(source, "new file occupying the original path");
    var undo2 = await archiver.UndoAsync(record2);
    Check("原位置被占用时改名放回", undo2.Success && record2.RestoredPath != source && File.Exists(record2.RestoredPath), record2.RestoredPath);
    Check("不覆盖已有文件", (await File.ReadAllTextAsync(source)).StartsWith("new file"));

    // 归档文件丢失 → 撤销失败
    var ghost = NewRecord(Path.Combine(inbox, "gone.docx"), FileSource.Global);
    ghost.Status = ArchiveStatus.Archived;
    ghost.ArchivedPath = Path.Combine(archiveRoot, "不存在.docx");
    var undo3 = await archiver.UndoAsync(ghost);
    Check("归档文件不在时报失败", !undo3.Success);

    // 日志
    var logPath = Path.Combine(root, "archive-log.jsonl");
    var log = new DeskFlow.Infrastructure.History.ArchiveLog(logPath);
    log.Archive("a.docx", "办公文档", "QQ", @"D:srca.docx", @"D:archive办公文档a.docx");
    log.Undo("a.docx", "办公文档", "QQ", @"D:archive办公文档a.docx", @"D:srca.docx");

    var tail = log.Tail(10);
    Check("日志写了两条", tail.Count == 2, tail.Count.ToString());
    Check("日志最新在前", tail[0].Action == "undo" && tail[1].Action == "archive");
    Check("日志保留原位置", tail[1].From == @"D:srca.docx");
}


// ---------- 16. 批次分组 ----------
Console.WriteLine("[16] ArchiveBatches");
{
    var now = DateTimeOffset.Now;
    FileRecord Make(string name, string category, string? batchId, int minutesAgo) => new()
    {
        Id = name,
        SourcePath = Path.Combine(@"D:\src", name),
        FileName = name,
        SizeBytes = 10,
        Source = FileSource.Qq,
        Category = category,
        Status = ArchiveStatus.Archived,
        ArchivedPath = Path.Combine(@"D:\archive", category, name),
        ArchivedAt = now.AddMinutes(-minutesAgo),
        BatchId = batchId,
    };

    var records = new List<FileRecord>
    {
        Make("a.docx", "办公文档", "b1", 1),
        Make("b.docx", "办公文档", "b1", 1),
        Make("c.pdf", "办公文档", "b1", 1),
        Make("d.png", "图片", "b2", 5),
        Make("e.png", "图片", "b2", 5),
    };

    var batches = ArchiveBatches.Group(records);
    Check("两条批次", batches.Count == 2, batches.Count.ToString());
    Check("按最近时间排序", batches[0].Category == "办公文档" && batches[0].Count == 3, batches[0].Display);
    Check("批次里带全部记录", batches[0].Records.Count == 3);
    Check("批次展示文本可读", batches[0].Display.Contains("办公文档") && batches[0].Display.Contains("3 个文件"));

    // 老记录没有 BatchId → 同类别 + 同一分钟兜底聚在一起
    var legacy = new List<FileRecord>
    {
        Make("x.docx", "办公文档", null, 2),
        Make("y.docx", "办公文档", null, 2),
        Make("z.png", "图片", null, 2),
    };
    var legacyBatches = ArchiveBatches.Group(legacy);
    Check("无批次号时按类别+分钟兜底", legacyBatches.Count == 2, legacyBatches.Count.ToString());
    Check("同类别两条归一批", legacyBatches.Any(b => b.Category == "办公文档" && b.Count == 2));
}

Console.WriteLine("[17] RecycleBin");
{
    var dir = NewTempDir();
    var file = Path.Combine(dir, "to-delete.txt");
    await File.WriteAllTextAsync(file, "bye");
    var ok = RecycleBin.TryDelete(file);
    Check("删除成功", ok);
    Check("文件真的没了", !File.Exists(file));
    Check("删不存在的文件返回 false", !RecycleBin.TryDelete(file));
}


// ---------- 18. 拖入指定分类 ----------
Console.WriteLine("[18] Drop into category");
{
    var root = NewTempDir();
    var inbox = Path.Combine(root, "inbox");
    var archiveRoot = Path.Combine(root, "archive");
    Directory.CreateDirectory(inbox);
    var archiver = new FileArchiver(new CategoryCatalog());

    var file = Path.Combine(inbox, "随手拍的图.png");
    await File.WriteAllTextAsync(file, "png-bytes");
    var record = NewRecord(file, FileSource.Manual);

    // 强制放到"我的收藏"（本来图片会进"图片"）
    var outcome = await archiver.ArchiveAsync(record, new ArchiveOptions
    {
        ArchiveRoot = archiveRoot,
        CategoryOverride = "我的收藏",
    });

    Check("拖入成功", outcome.Success, outcome.Error);
    Check("落在指定分类里", record.Category == "我的收藏", record.Category);
    Check("路径在 我的收藏 目录下",
        record.ArchivedPath!.Contains(Path.Combine("archive", "我的收藏")), record.ArchivedPath);
    Check("默认是复制，原件还在", File.Exists(file));

    // 不指定分类时仍然按扩展名自动分类
    var auto = NewRecord(file, FileSource.Manual);
    await archiver.ArchiveAsync(auto, new ArchiveOptions { ArchiveRoot = archiveRoot });
    Check("不指定时自动分类", auto.Category == "图片", auto.Category);
}


// ---------- 19. 内容分类 ----------
Console.WriteLine("[19] Content classifier");
{
    var root = NewTempDir();
    var archiveRoot = Path.Combine(root, "archive");
    var classifier = new DeskFlow.Infrastructure.Analysis.KeywordContentClassifier();

    string MakeDocx(string name, string body)
    {
        var path = Path.Combine(root, name);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = zip.CreateEntry("word/document.xml");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($"<?xml version=\"1.0\"?><w:document><w:body><w:p><w:r><w:t>{body}</w:t></w:r></w:p></w:body></w:document>");
        return path;
    }

    var invoice = MakeDocx("scan01.docx",
        "增值税专用发票 开票日期 2026-11-20 价税合计 1130.00 纳税人识别号 91500000XXXX 销售方 购买方");
    var invoiceResult = classifier.Classify(invoice, "scan01.docx");
    Check("发票正文识别为发票", invoiceResult?.Category == "发票", invoiceResult?.Category + " / " + invoiceResult?.Reason);

    var contract = MakeDocx("agreement.docx",
        "劳动合同 甲方 TidyFlow 科技有限公司 乙方 张三 签署日期 违约责任 本协议一式两份");
    var contractResult = classifier.Classify(contract, "agreement.docx");
    Check("合同正文识别为合同", contractResult?.Category == "合同", contractResult?.Category + " / " + contractResult?.Reason);

    var noise = Path.Combine(root, "note.txt");
    await File.WriteAllTextAsync(noise, "hello world, nothing special here at all");
    Check("内容判不出来时返回 null", classifier.Classify(noise, "note.txt") is null);

    // 归档器：内容分类 → 自动建目录
    var archiver = new FileArchiver(new CategoryCatalog(), classifier);
    var record = NewRecord(invoice, FileSource.Manual);
    var outcome = await archiver.ArchiveAsync(record, new ArchiveOptions { ArchiveRoot = archiveRoot });
    Check("按内容归档成功", outcome.Success, outcome.Error);
    Check("落到 发票 目录", record.Category == "发票" && record.ArchivedPath!.Contains("发票"), record.ArchivedPath);
    Check("目录被自动创建", Directory.Exists(Path.Combine(archiveRoot, "发票")));
    Check("记录了分类依据", record.CategoryReason?.StartsWith("内容识别") == true, record.CategoryReason);

    // 关闭开关 → 回到扩展名规则
    var record2 = NewRecord(invoice, FileSource.Manual);
    await archiver.ArchiveAsync(record2, new ArchiveOptions { ArchiveRoot = archiveRoot, UseContentClassification = false });
    Check("关掉开关后按扩展名", record2.Category == "办公文档", record2.Category);
}


// ---------- 20. 按盘扫描 ----------
Console.WriteLine("[20] FileScanner");
{
    var root = NewTempDir();
    var docs = Path.Combine(root, "Docs");
    var windows = Path.Combine(root, "Windows");
    var programFiles = Path.Combine(root, "Program Files");
    Directory.CreateDirectory(docs);
    Directory.CreateDirectory(windows);
    Directory.CreateDirectory(programFiles);

    var fresh = Path.Combine(docs, "报告-最近.docx");
    await File.WriteAllTextAsync(fresh, new string('x', 4096));
    var old = Path.Combine(docs, "报告-很久以前.docx");
    await File.WriteAllTextAsync(old, new string('x', 4096));
    File.SetLastWriteTime(old, DateTime.Now.AddDays(-120));

    var image = Path.Combine(docs, "截图.png");
    await File.WriteAllTextAsync(image, new string('x', 4096));

    var tiny = Path.Combine(docs, "空.txt");
    await File.WriteAllTextAsync(tiny, "x");

    await File.WriteAllTextAsync(Path.Combine(windows, "system.dll"), new string('x', 4096));
    await File.WriteAllTextAsync(Path.Combine(programFiles, "app.exe"), new string('x', 4096));

    var all = FileScanner.Scan(new ScanOptions { Roots = [root] });
    Check("扫到用户文件", all.Any(i => i.Path.EndsWith("报告-最近.docx")), all.Count.ToString());
    Check("系统目录被跳过",
        !all.Any(i => i.Path.Contains("Windows") || i.Path.Contains("Program Files")));
    Check("小于 1KB 的被过滤", !all.Any(i => i.Path.EndsWith("空.txt")));

    var docxOnly = FileScanner.Scan(new ScanOptions { Roots = [root], Extensions = [".docx"] });
    Check("按扩展名过滤", docxOnly.Count == 2 && docxOnly.All(i => i.Path.EndsWith(".docx")), docxOnly.Count.ToString());

    var recent = FileScanner.Scan(new ScanOptions
    {
        Roots = [root],
        Extensions = [".docx"],
        ModifiedAfter = DateTime.Now.AddDays(-30),
    });
    Check("按修改时间过滤", recent.Count == 1 && recent[0].Path == fresh, recent.Count.ToString());

    var download = FileScanner.Scan(new ScanOptions { Roots = [root], MaxResults = 1 });
    Check("结果条数可截断", download.Count == 1);

    var emptyDir = Path.Combine(root, "Archive");
    Directory.CreateDirectory(emptyDir);
    var excluded = FileScanner.Scan(new ScanOptions { Roots = [root], ExcludedPaths = [emptyDir] });
    Check("排除目录生效（自身没文件，不影响结果）", excluded.Count == all.Count);
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