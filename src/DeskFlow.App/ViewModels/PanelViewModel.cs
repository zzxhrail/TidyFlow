using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskFlow.App.Services;
using DeskFlow.Core.Categorization;
using DeskFlow.Core.Models;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Archive;
using DeskFlow.Infrastructure.Index;
using DeskFlow.Infrastructure.Settings;
using DeskFlow.Infrastructure.Watch;

namespace DeskFlow.App.ViewModels;

/// <summary>
/// 桌面面板的大脑：订阅监听器、决定"自动归档还是等用户点"、维护最近/待接收/分类/快捷方式四块列表。
/// 所有 UI 集合只在 Dispatcher 线程上改。
/// </summary>
public sealed class PanelViewModel : ObservableObject
{
    private readonly ArchiveSettings _settings;
    private readonly JsonArchiveIndex _index;
    private readonly CategoryCatalog _categories;
    private readonly FileArchiver _archiver;
    private IncomingWatcher? _watcher;
    private GlobalFileWatcher? _globalWatcher;
    private string _status = "就绪";
    private string _archiveRoot;
    private int _pendingCount;

    public PanelViewModel(ArchiveSettings settings, JsonArchiveIndex index, CategoryCatalog categories)
    {
        _settings = settings;
        _index = index;
        _categories = categories;
        _archiver = new FileArchiver(categories);
        _archiveRoot = settings.ArchiveRoot;

        BuildSourceToggles();
        RefreshAll();
    }

    public ObservableCollection<SourceToggle> Sources { get; } = new();

    public ObservableCollection<PanelTile> Shortcuts { get; } = new();

    public ObservableCollection<PanelTile> RecentFiles { get; } = new();

    public ObservableCollection<PanelTile> PendingFiles { get; } = new();

    public ObservableCollection<PanelTile> Categories { get; } = new();

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ArchiveRoot
    {
        get => _archiveRoot;
        private set => SetProperty(ref _archiveRoot, value);
    }

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (SetProperty(ref _pendingCount, value))
            {
                OnPropertyChanged(nameof(HasPending));
            }
        }
    }

    public bool HasPending => _pendingCount > 0;

    public bool HasRecent { get; private set; }

    private int _columns = 4;
    private double _tileWidth = 70;
    private double _tileHeight = 88;
    private double _iconSize = 30;
    private double _tileFontSize = 10.5;
    private double _titleFontSize = 9;
    private bool _showSubtitle = true;

    /// <summary>每行格子数，由面板宽度算出（2–8 列）。</summary>
    public int Columns
    {
        get => _columns;
        private set => SetProperty(ref _columns, value);
    }

    public double TileWidth
    {
        get => _tileWidth;
        private set => SetProperty(ref _tileWidth, value);
    }

    public double TileHeight
    {
        get => _tileHeight;
        private set => SetProperty(ref _tileHeight, value);
    }

    public double IconSize
    {
        get => _iconSize;
        private set => SetProperty(ref _iconSize, value);
    }

    public double TileFontSize
    {
        get => _tileFontSize;
        private set => SetProperty(ref _tileFontSize, value);
    }

    public double SubtitleFontSize
    {
        get => _titleFontSize;
        private set => SetProperty(ref _titleFontSize, value);
    }

    /// <summary>标题行高与最多两行的高度，保证不同字号下都不会压到副标题。</summary>
    private double _lineHeight = 13;
    private double _maxTitleHeight = 27;

    public double TileLineHeight => _lineHeight;

    public double TitleMaxHeight => _maxTitleHeight;

    public bool ShowSubtitle
    {
        get => _showSubtitle;
        private set => SetProperty(ref _showSubtitle, value);
    }

    /// <summary>
    /// 面板密度自适应：宽度变了就重算列数、格子尺寸、字号。
    /// 窄了最少 2 列（还看得清文件名），宽了最多 8 列（再宽就不好扫视了）。
    /// </summary>
    public void ApplyPanelWidth(double panelWidth)
    {
        if (double.IsNaN(panelWidth) || panelWidth <= 0)
        {
            return;
        }

        const double horizontalPadding = 44;   // 窗口内边距 + 边框 + 格子间距
        const double minTileWidth = 80;
        var usable = Math.Max(160, panelWidth - horizontalPadding);
        var columns = Math.Clamp((int)Math.Floor(usable / minTileWidth), 2, 8);
        // 扣掉格子外边距（3×2）与按钮内边距（4×2），否则文字会被 ClipToBounds 切掉
        var tileWidth = Math.Max(64, Math.Floor(usable / columns) - 14);
        var scale = Math.Clamp(tileWidth / 70.0, 0.8, 1.6);

        var iconSize = Math.Round(30 * Math.Clamp(scale, 0.85, 1.6), 1);
        var titleFont = Math.Round(10.5 * Math.Clamp(scale, 0.9, 1.35), 1);
        var subtitleFont = Math.Round(9 * Math.Clamp(scale, 0.9, 1.2), 1);
        var showSubtitle = tileWidth >= 82;
        var lineHeight = Math.Round(titleFont * 1.35, 1);
        var subtitleLine = Math.Round(subtitleFont * 1.35, 1);

        // 高度 = 图标 + 间距 + 两行标题 + 副标题，全部按真实字号算，避免文字被切
        var tileHeight = Math.Round(iconSize + 9 + (lineHeight * 2) + (showSubtitle ? subtitleLine + 3 : 2), 1);

        var changed = columns != _columns
            || Math.Abs(tileWidth - _tileWidth) > 1
            || Math.Abs(iconSize - _iconSize) > 0.5;
        if (!changed)
        {
            return;
        }

        Columns = columns;
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        IconSize = iconSize;
        TileFontSize = titleFont;
        SubtitleFontSize = subtitleFont;
        ShowSubtitle = showSubtitle;
        _lineHeight = lineHeight;
        _maxTitleHeight = Math.Round(lineHeight * 2 + 2, 1);
        OnPropertyChanged(nameof(TileLineHeight));
        OnPropertyChanged(nameof(TitleMaxHeight));

        // 尺寸变了就按新尺寸重建四个列表（几十个格子，成本可忽略，行为最可预测）
        Diagnostics.Log($"width={panelWidth:F1} usable={usable:F1} columns={columns} tile={tileWidth:F1} icon={iconSize:F1} height={tileHeight:F1} scale={scale:F2}");
        BuildShortcuts();
        BuildPending();
        BuildRecent();
        BuildCategories();
    }

    public ObservableCollection<string> Logs { get; } = new();

    public string LogText => string.Join(Environment.NewLine, Logs.TakeLast(3));

    public void StartWatching()
    {
        StopWatching();
        var targets = BuildTargets();
        _watcher = new IncomingWatcher(targets);
        _watcher.FileDetected += OnFileDetected;
        _watcher.PrimeExisting();
        _watcher.Start();
        Log($"监听 {targets.Count} 个目录，已就绪");
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    /// 全局监听：整机任意目录落盘就搬走。默认关闭，用户勾选后才启动。
    /// 排除项里塞了聊天工具目录、归档根、配置目录，避免重复归档与自噬。
    /// </summary>
    public void StartGlobalWatch()
    {
        Services.Diagnostics.Log($"global watch start enabled={_settings.GlobalWatchEnabled} roots={_settings.GlobalRoots.Count} ext={_settings.GlobalExtensions.Count}");
        StopGlobalWatch();

        var options = GlobalWatchOptionsFactory.Build(
            _settings,
            installDirectory: Path.GetDirectoryName(Environment.ProcessPath),
            extraExcluded: BuildTargets().Select(static t => t.Directory));

        if (!options.Enabled)
        {
            Log("全局监听：未开启");
            return;
        }

        _globalWatcher = new GlobalFileWatcher();
        _globalWatcher.FileDetected += path => OnUi(() => _ = HandleGlobalFileAsync(path));
        _globalWatcher.Start(options);

        Log($"全局监听：{options.Roots.Count} 个根目录，{options.Extensions.Count} 种类型，命中即移动归档");
        Services.Diagnostics.Log($"global watch roots=[{string.Join(",", options.Roots)}] ext=[{string.Join(",", options.Extensions)}]");
    }

    public void StopGlobalWatch()
    {
        _globalWatcher?.Dispose();
        _globalWatcher = null;
    }

    public void RestartGlobalWatch()
    {
        StartGlobalWatch();
    }

    /// <summary>全局监听命中：先弹提示，再移动归档（原文件会被移走，不留副本）。</summary>
    private async Task HandleGlobalFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path) || _index.KnowsSourcePath(path))
            {
                return;
            }

            var info = new FileInfo(path);
            var category = _categories.Resolve(info.Name);
            var record = new FileRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                SourcePath = info.FullName,
                FileName = info.Name,
                SizeBytes = info.Length,
                Source = FileSource.Global,
                Category = category.Name,
            };

            ToastService.Show("发现新文件，正在归档", $"{info.Name} → {category.Name}", info.FullName);

            var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
            {
                ArchiveRoot = _settings.ArchiveRoot,
                MoveInsteadOfCopy = true,
                SkipDuplicates = true,
            });

            _index.Upsert(outcome.Record);

            if (outcome.Duplicate)
            {
                // 归档里已有同内容文件：按"移动"的语义把散落的那份删掉
                TryDelete(path);
                ToastService.Show("已存在相同文件，原件已删除", info.Name, outcome.Record.ArchivedPath);
                Log($"全局：{info.Name} 与已有文件重复，已删除原件");
            }
            else if (outcome.Success)
            {
                ToastService.Show($"已归档到「{record.Category}」", $"{info.Name}（原位置已移走）", record.ArchivedPath);
                Log($"全局：{info.Name} → {record.Category}");
            }
            else
            {
                ToastService.Show("归档失败", $"{info.Name}：{outcome.Error}", info.FullName);
                Log($"全局：{info.Name} 归档失败 {outcome.Error}");
            }

            BuildRecent();
            BuildCategories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"全局：处理 {path} 失败 {ex.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"全局：删除原件失败 {Path.GetFileName(path)} {ex.Message}");
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>补捞最近一段时间内、索引里还没有的文件（覆盖"软件没开着的时候收到的文件"）。</summary>
    public void ImportRecent(TimeSpan window)
    {
        var since = DateTime.Now - window;
        var imported = 0;
        foreach (var target in BuildTargets())
        {
            if (!Directory.Exists(target.Directory))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(target.Directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 3,
                }))
                {
                    var info = new FileInfo(file);
                    // 补扫也要走同样的噪声过滤，否则启动瞬间会把 QQ 日志、缩略图、
                    // 微信 apm 记录当成"收到的文件"归档进来。
                    if (info.LastWriteTime < since || _index.KnowsSourcePath(file) || IncomingWatcher.IsNoise(file, target.Directory))
                    {
                        continue;
                    }

                    HandleDetected(new IncomingFile(file, target.Source, DateTimeOffset.Now));
                    // 补扫过的文件要让监听器也记住，否则下一轮轮询会再上报一次（重复归档）
                    _watcher?.MarkKnown(file);
                    imported++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (imported > 0)
        {
            Log($"补捞到 {imported} 个新文件");
        }
    }

    public void RefreshAll()
    {
        BuildShortcuts();
        BuildRecent();
        BuildPending();
        BuildCategories();
        Status = $"{_index.All.Count} 条记录 · 归档到 {Path.GetFileName(_archiveRoot)}";
    }

    public void SetAutoDownload(SourceToggle toggle, bool enabled)
    {
        if (!Enum.TryParse<FileSource>(toggle.SourceKey, out var source))
        {
            source = FileSource.Unknown;
        }

        SetAutoDownload(source, enabled);
    }

    /// <summary>按来源改"自动下载"，同时把面板顶部的勾选状态同步过去（托盘菜单也会调这里）。</summary>
    public void SetAutoDownload(FileSource source, bool enabled)
    {
        _settings.SetAutoDownload(source, enabled);

        var toggle = Sources.FirstOrDefault(t => string.Equals(t.SourceKey, source.ToString(), StringComparison.OrdinalIgnoreCase));
        if (toggle is not null && toggle.AutoDownload != enabled)
        {
            toggle.AutoDownload = enabled;
        }

        Log($"{source.DisplayName()} 自动下载 = {(enabled ? "开" : "关")}");
    }

    public async Task AcceptPendingAsync(PanelTile tile)
    {
        var record = _index.All.FirstOrDefault(r => r.Id == tile.RecordId);
        if (record is null)
        {
            return;
        }

        var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
        {
            ArchiveRoot = _settings.ArchiveRoot,
            MoveInsteadOfCopy = _settings.MoveInsteadOfCopy,
        });

        _index.Upsert(outcome.Record);
        Log(outcome.Success ? $"已归档 {record.FileName} → {record.Category}" : $"归档失败 {record.FileName}: {outcome.Error}");
        BuildRecent();
        BuildPending();
        BuildCategories();
    }

    public void IgnorePending(PanelTile tile)
    {
        var record = _index.All.FirstOrDefault(r => r.Id == tile.RecordId);
        if (record is null)
        {
            return;
        }

        record.Status = ArchiveStatus.Ignored;
        _index.Upsert(record);
        BuildPending();
    }

    public void AddShortcut(string name, string path, bool isFolder)
    {
        _settings.Shortcuts.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.Shortcuts.Add(new ShortcutItem { Name = name, Path = path, IsFolder = isFolder });
        _settings.Save();
        BuildShortcuts();
    }

    public void RemoveShortcut(string path)
    {
        _settings.Shortcuts.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        BuildShortcuts();
    }

    public IReadOnlyList<WatchTarget> BuildTargets()
    {
        var list = new List<WatchTarget>();
        list.AddRange(SourceDirectoryResolver.Discover(cachedRoots: _settings.DiscoveryRoots));
        foreach (var extra in _settings.ExtraTargets)
        {
            if (Directory.Exists(extra.Path))
            {
                list.Add(new WatchTarget(extra.Name, extra.Path, FileSource.Manual, IsDetected: false));
            }
        }

        return list;
    }

    public void AddWatchDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (_settings.ExtraTargets.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _settings.ExtraTargets.Add(new ShortcutItem { Name = new DirectoryInfo(path).Name, Path = path, IsFolder = true });
        _settings.Save();
        StartWatching();
        Log($"新增监听目录 {path}");
    }

    public void RemoveWatchDirectory(string path)
    {
        _settings.ExtraTargets.RemoveAll(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        StartWatching();
    }

    public void UpdateArchiveRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        _settings.ArchiveRoot = path;
        _settings.Save();
        ArchiveRoot = path;
        Status = $"归档根目录已切到 {path}";
    }

    private void OnFileDetected(IncomingFile incoming)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            HandleDetected(incoming);
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() => HandleDetected(incoming)), DispatcherPriority.Background);
    }

    private void HandleDetected(IncomingFile incoming)
    {
        if (!File.Exists(incoming.FullPath))
        {
            return;
        }

        // 同一条路径只处理一次：启动补扫与轮询可能同时看到同一个文件
        if (_index.KnowsSourcePath(incoming.FullPath))
        {
            return;
        }

        var info = new FileInfo(incoming.FullPath);
        var category = _categories.Resolve(incoming.FullPath);
        var record = new FileRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SourcePath = incoming.FullPath,
            FileName = info.Name,
            SizeBytes = info.Length,
            Source = incoming.Source,
            Category = category.Name,
            DetectedAt = incoming.DetectedAt,
        };

        if (_settings.IsAutoDownload(incoming.Source))
        {
            _ = ArchiveAndRecordAsync(record);
            return;
        }

        record.Status = ArchiveStatus.Pending;
        _index.Upsert(record);
        Log($"待接收：{incoming.Source.DisplayName()} · {info.Name}");
        BuildPending();
        BuildRecent();
    }

    private async Task ArchiveAndRecordAsync(FileRecord record)
    {
        var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
        {
            ArchiveRoot = _settings.ArchiveRoot,
            MoveInsteadOfCopy = _settings.MoveInsteadOfCopy,
        });

        var dispatcher = Application.Current?.Dispatcher;
        void Apply()
        {
            _index.Upsert(outcome.Record);
            Log(outcome.Success
                ? $"自动归档 {record.FileName} → {record.Category}"
                : $"归档失败 {record.FileName}: {outcome.Error}");
            BuildRecent();
            BuildCategories();
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _ = dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Background);
        }
    }

    /// <summary>按当前密度参数造一个格子，四个列表共用，避免每处都手写尺寸。</summary>
    private PanelTile MakeTile(TileKind kind, string title, string subtitle, System.Windows.Media.ImageSource? icon, string? path = null, string? recordId = null) =>
        new()
        {
            Title = title,
            Subtitle = subtitle,
            Icon = icon,
            Path = path,
            Kind = kind,
            RecordId = recordId,
            BoxWidth = TileWidth,
            BoxHeight = TileHeight,
            IconSize = IconSize,
            TitleFontSize = TileFontSize,
            SubtitleFontSize = SubtitleFontSize,
            LineHeight = _lineHeight,
            MaxTitleHeight = _maxTitleHeight,
            ShowSubtitle = ShowSubtitle && !string.IsNullOrEmpty(subtitle),
            TitleText = FitFileName(title, TileWidth - 4, TileFontSize, 2),
            SubtitleText = FitText(subtitle, TileWidth - 4, SubtitleFontSize, 1),
        };

    /// <summary>
    /// 按格子宽度裁字：中日韩字符按 1 个字号宽计，ASCII 按 0.56 个字号宽计，
    /// 超出预算就截断加省略号。比依赖 TextTrimming 更可控 —— 换 DPI、换字体都不会出现半个字。
    /// </summary>
    private static string FitText(string text, double width, double fontSize, int maxLines)
    {
        if (string.IsNullOrEmpty(text) || width <= 0 || fontSize <= 0)
        {
            return text;
        }

        // 0.92 是安全余量：贴边会换来"半个字被裁掉"，宁可提前省略
        var budget = Math.Max(2, (int)Math.Floor(width / fontSize * maxLines * 0.92));
        var used = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            used += IsWide(text[i]) ? 1.0 : 0.62;
            if (used > budget)
            {
                return text[..Math.Max(1, i - 1)].TrimEnd() + "…";
            }
        }

        return text;
    }

    /// <summary>
    /// 文件名专属排版：一行放不下时优先在扩展名前换行（"会议纪要.docx" → 两行），
    /// 比在词中间被裁掉好看，也避免 WPF 对无空格长串"换不了行就溢出去"的问题。
    /// </summary>
    private static string FitFileName(string name, double width, double fontSize, int maxLines)
    {
        var single = Math.Max(3, (int)Math.Floor(width / fontSize * 0.92));
        if (EstimateUnits(name) <= single || maxLines < 2)
        {
            return FitText(name, width, fontSize, maxLines);
        }

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return FitText(name, width, fontSize, maxLines);
        }

        var stem = FitText(name[..dot], width, fontSize, 1);
        var extension = name[dot..];
        return stem + Environment.NewLine + extension;
    }

    private static double EstimateUnits(string text)
    {
        var used = 0.0;
        foreach (var c in text)
        {
            used += IsWide(c) ? 1.0 : 0.62;
        }

        return used;
    }

    private static bool IsWide(char c) =>
        c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3)
        || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60)
        || (c >= 0xFFE0 && c <= 0xFFE6));

    private void BuildSourceToggles()
    {
        Sources.Clear();
        var targets = BuildTargets();
        foreach (var source in new[] { FileSource.Qq, FileSource.WeChat, FileSource.DingTalk, FileSource.Downloads })
        {
            var matched = targets.Where(t => t.Source == source).ToList();
            var detected = matched.Count(t => t.IsDetected);

            var detail = detected > 0
                ? (detected == 1 ? "已检测到目录" : $"已检测到 {detected} 个目录")
                : matched.Count > 0
                    ? "已装好，等第一个文件落盘"   // 客户端装了就一定会有，只是目录还没被创建
                    : "未检测到（可在设置里手动添加）";

            Sources.Add(new SourceToggle(source.DisplayName(), detail, source.ToString(), matched.Count > 0, _settings.IsAutoDownload(source)));
        }
    }

    /// <summary>
    /// 监听诊断：每个目录是否可读、最近 24 小时有多少个"非噪声"新文件、最新一个是什么。
    /// 用来回答"到底是我没监听到，还是客户端根本没落盘"。
    /// </summary>
    public async Task<IReadOnlyList<WatchDiagnostic>> DiagnoseAsync()
    {
        var targets = BuildTargets();
        var probes = await Task.Run(() => WatchDiagnostics.Probe(targets, TimeSpan.FromHours(24)));
        return probes
            .Select(p => new WatchDiagnostic(p.Name, p.Path, p.Exists, p.Detected, p.RecentCount, p.LatestName, p.LatestTime))
            .ToList();
    }

    /// <summary>手动归档：自动监听没接住时，用户自己选文件进来（走同一套分类/判重）。</summary>
    public async Task<int> ImportFilesAsync(IEnumerable<string> paths)
    {
        var archived = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var info = new FileInfo(path);
            var record = new FileRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                SourcePath = info.FullName,
                FileName = info.Name,
                SizeBytes = info.Length,
                Source = FileSource.Manual,
                Category = _categories.Resolve(info.Name).Name,
            };

            var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
            {
                ArchiveRoot = _settings.ArchiveRoot,
                MoveInsteadOfCopy = _settings.MoveInsteadOfCopy,
            });

            _index.Upsert(outcome.Record);
            if (outcome.Success)
            {
                archived++;
                Log($"手动归档 {record.FileName} → {record.Category}");
            }
        }

        RefreshAll();
        return archived;
    }

    /// <summary>兜底扫描盘符找数据目录，结果缓存进配置，然后重启监听。</summary>
    public async Task<string> ScanForDataRootsAsync()
    {
        var found = await Task.Run(() => DriveScanner.Scan(3));
        var added = 0;
        foreach (var path in found)
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parent))
            {
                continue;
            }

            if (!_settings.DiscoveryRoots.Contains(parent, StringComparer.OrdinalIgnoreCase))
            {
                _settings.DiscoveryRoots.Add(parent);
                added++;
            }
        }

        if (added > 0)
        {
            _settings.Save();
        }

        BuildSourceToggles();
        StartWatching();
        RefreshAll();
        Log(added == 0 ? "扫描完成，没有发现新的数据目录" : $"扫描完成，新增 {added} 个数据根目录");
        return added == 0 ? "没有发现新的客户端数据目录" : $"新增 {added} 个数据根目录";
    }

    private void BuildShortcuts()
    {
        Shortcuts.Clear();
        foreach (var item in _settings.Shortcuts)
        {
            if (!(File.Exists(item.Path) || Directory.Exists(item.Path)))
            {
                continue;
            }

            Shortcuts.Add(MakeTile(
                item.IsFolder ? TileKind.Category : TileKind.Shortcut,
                item.Name,
                item.IsFolder ? "文件夹" : "应用",
                ShellIconProvider.ForPath(item.Path),
                item.Path));
        }

        if (Shortcuts.Count == 0)
        {
            foreach (var seed in ShortcutSeeder.Seed())
            {
                Shortcuts.Add(MakeTile(
                    seed.IsFolder ? TileKind.Category : TileKind.Shortcut,
                    seed.Name,
                    seed.IsFolder ? "文件夹" : "应用",
                    ShellIconProvider.ForPath(seed.Path),
                    seed.Path));
            }
        }

        Shortcuts.Add(MakeTile(
            TileKind.Add,
            "添加",
            "快捷方式",
            ShellIconProvider.ForFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows))));
    }

    private void BuildRecent()
    {
        RecentFiles.Clear();
        foreach (var record in _index.Recent(8))
        {
            RecentFiles.Add(MakeTile(
                TileKind.File,
                record.FileName,
                $"{record.Category} · {record.Source.DisplayName()}",
                ShellIconProvider.ForFile(record.ArchivedPath ?? record.SourcePath),
                record.DisplayPath));
        }

        HasRecent = RecentFiles.Count > 0;
        OnPropertyChanged(nameof(HasRecent));
    }

    private void BuildPending()
    {
        PendingFiles.Clear();
        foreach (var record in _index.Pending().Take(8))
        {
            PendingFiles.Add(MakeTile(
                TileKind.Pending,
                record.FileName,
                $"{record.Source.DisplayName()} · 点我归档",
                ShellIconProvider.ForFile(record.SourcePath),
                record.SourcePath,
                record.Id));
        }

        PendingCount = _index.Pending().Count;
        OnPropertyChanged(nameof(HasPending));
    }

    private void BuildCategories()
    {
        Categories.Clear();
        var records = _index.All.Where(static r => r.Status == ArchiveStatus.Archived).ToList();
        foreach (var category in _categories.All)
        {
            var directory = Path.Combine(_settings.ArchiveRoot, FileArchiver.SanitizeFolderName(category.Name));
            var count = records.Count(r => string.Equals(r.Category, category.Name, StringComparison.OrdinalIgnoreCase));
            if (count == 0 && !Directory.Exists(directory))
            {
                continue;
            }

            Categories.Add(MakeTile(
                TileKind.Category,
                category.Name,
                $"{count} 个",
                ShellIconProvider.ForFolder(directory),
                directory));
        }
    }


    private void Log(string message)
    {
        Logs.Add($"{DateTime.Now:HH:mm:ss} {message}");
        while (Logs.Count > 200)
        {
            Logs.RemoveAt(0);
        }

        OnPropertyChanged(nameof(LogText));
    }
}