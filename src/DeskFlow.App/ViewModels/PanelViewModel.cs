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
    private readonly DeskFlow.Infrastructure.History.ArchiveLog _archiveLog = new();

    // 批量归档的判定：同一类别、间隔 15 秒内的连续归档算同一批
    private string? _lastBatchId;
    private string? _lastBatchCategory;
    private DateTimeOffset _lastBatchAt;
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
        _archiver = new FileArchiver(categories, new DeskFlow.Infrastructure.Analysis.KeywordContentClassifier());
        _archiveRoot = settings.ArchiveRoot;

        BuildSourceToggles();
        RefreshAll();
    }

    public ObservableCollection<SourceToggle> Sources { get; } = new();

    public ObservableCollection<PanelTile> Shortcuts { get; } = new();

    public ObservableCollection<PanelTile> RecentFiles { get; } = new();

    public ObservableCollection<PanelTile> PendingFiles { get; } = new();

    public ObservableCollection<PanelTile> Categories { get; } = new();

    /// <summary>收起条里横向排列的图标（固定位置 + 用户快捷方式）。</summary>
    public ObservableCollection<PanelTile> CollapsedTiles { get; } = new();

    /// <summary>固定位置（归档目录）：不跟用户快捷方式混在一起，永远在「应用与快捷方式」顶部。</summary>
    public ObservableCollection<PanelTile> SystemPlaces { get; } = new();

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


    /// <summary>新建分类：写进用户类别、在归档根建目录、刷新导航与分类网格。</summary>
    public bool CreateCategory(string name, out string message)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            message = "分类名不能为空";
            return false;
        }

        if (_categories.All.Any(c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            message = "已经有这个分类了";
            return false;
        }

        _settings.UserCategories.Add([clean]);
        _settings.Save();
        _categories.UpsertUserCategory(clean, Array.Empty<string>());

        TryCreateArchiveFolder(clean);
        BuildCategories();
        RebuildNav();
        RaiseConfirmed($"已新建分类「{clean}」");
        Log($"已新建分类「{clean}」");
        message = clean;
        return true;
    }

    /// <summary>重命名用户自定义分类（内置分类不允许改名）。</summary>
    public bool RenameCategory(string oldName, string newName, out string message)
    {
        var clean = (newName ?? string.Empty).Trim();
        if (clean.Length == 0 || string.Equals(oldName, clean, StringComparison.OrdinalIgnoreCase))
        {
            message = "名字没变";
            return false;
        }

        var index = _settings.UserCategories.FindIndex(c => c.Length > 0 && string.Equals(c[0], oldName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            message = "只有自己新建的分类可以改名";
            return false;
        }

        var extensions = _settings.UserCategories[index].Skip(1).ToArray();
        _settings.UserCategories[index] = [clean, .. extensions];
        _settings.Save();
        _categories.RenameUserCategory(oldName, clean);

        // 目录跟着改名（存在才改，避免误建空目录）
        var oldDir = Path.Combine(_settings.ArchiveRoot, oldName);
        var newDir = Path.Combine(_settings.ArchiveRoot, clean);
        try
        {
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
            {
                Directory.Move(oldDir, newDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"分类目录改名失败：{ex.Message}");
        }

        BuildCategories();
        RebuildNav();
        Log($"分类「{oldName}」改名为「{clean}」");
        message = clean;
        return true;
    }

    /// <summary>删除用户自定义分类（只删规则，磁盘上的文件不动）。</summary>
    public bool RemoveCategory(string name, out string message)
    {
        var index = _settings.UserCategories.FindIndex(c => c.Length > 0 && string.Equals(c[0], name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            message = "只有自己新建的分类可以删除";
            return false;
        }

        _settings.UserCategories.RemoveAt(index);
        _settings.Save();
        _categories.RemoveUserCategory(name);
        BuildCategories();
        RebuildNav();
            RaiseConfirmed("已删除（回收站）");
        Log($"已删除分类规则「{name}」（磁盘上的文件没动）");
        message = name;
        return true;
    }

    /// <summary>把任意文件夹固定到一级导航。</summary>
    public bool AddNavItem(string name, string path, out string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            message = "目录不存在";
            return false;
        }

        var title = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : name.Trim();
        _settings.UserNavItems.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        _settings.UserNavItems.Add(new ShortcutItem { Name = title, Path = path, IsFolder = true });
        _settings.Save();
        RebuildNav();
        Log($"已添加导航项「{title}」");
        message = title;
        return true;
    }

    public bool RemoveNavItem(string path)
    {
        var removed = _settings.UserNavItems.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            _settings.Save();
            RebuildNav();
            Log("已移除该导航项");
        }

        return removed;
    }

    private void TryCreateArchiveFolder(string category)
    {
        try
        {
            var dir = Path.Combine(_settings.ArchiveRoot, FileArchiver.SanitizeFolderName(category));
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"建目录失败：{ex.Message}");
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
        // 左侧导航固定占 128 + 12 间距 + 边距，图标区只能用剩下的宽度
        const double navWidth = 128 + 12;
        var usable = Math.Max(140, panelWidth - horizontalPadding - navWidth);
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

        // 原位重排：只改已有格子的尺寸/文字，列表本身不动
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var relaid = RelayoutTiles();
        stopwatch.Stop();
        Diagnostics.Log($"width={panelWidth:F1} usable={usable:F1} columns={columns} tile={tileWidth:F1} icon={iconSize:F1} height={tileHeight:F1} scale={scale:F2} relayout={relaid} cost={stopwatch.Elapsed.TotalMilliseconds:F2}ms");
    }

    /// <summary>左侧导航的一条（分组标题或可点击条目）。</summary>
    public sealed record NavEntry(string Title, string Glyph, string Kind, string Key, bool IsHeader = false, double Indent = 0, string Badge = "")
    {
        /// <summary>二级条目缩进（Thickness 不能直接绑定 double）。</summary>
        public Thickness Margin => new(Indent, 0, 0, 0);

        public override string ToString() => Title;
    }


    /// <summary>启动补扫期间为 true：这期间识别到的历史文件不弹系统通知。</summary>
    public bool SuppressNotifications { get; set; }

    private string _section = "shortcuts";

    /// <summary>当前显示的分区：shortcuts / recent / category。</summary>
    public string Section
    {
        get => _section;
        private set
        {
            if (SetProperty(ref _section, value))
            {
                OnPropertyChanged(nameof(IsShortcutsSection));
                OnPropertyChanged(nameof(IsRecentSection));
                OnPropertyChanged(nameof(IsCategorySection));
            }
        }
    }

    public bool IsShortcutsSection => _section == "shortcuts";

    public bool IsRecentSection => _section == "recent";

    public bool IsCategorySection => _section == "category";

    /// <summary>一级导航：应用与快捷方式 / 最近归档 / 分类。</summary>
    public ObservableCollection<NavEntry> RootNav { get; } = new();

    /// <summary>二级导航：某个一级项（目前是「分类」）下面的条目。</summary>
    public ObservableCollection<NavEntry> CategoryNav { get; } = new();

    private bool _navInCategories;

    /// <summary>导航是否停在二级（分类列表）。</summary>
    public bool IsNavCategories
    {
        get => _navInCategories;
        private set
        {
            if (SetProperty(ref _navInCategories, value))
            {
                OnPropertyChanged(nameof(IsNavRoot));
            }
        }
    }

    public bool IsNavRoot => !_navInCategories;

    public void OpenCategoryNav() => IsNavCategories = true;

    public void BackToRootNav() => IsNavCategories = false;

    public void SelectShortcuts() => Section = "shortcuts";

    public void SelectRecent() => Section = "recent";

    private bool _isBrowsing;
    private string _browseTitle = string.Empty;
    private string _browseSubtitle = string.Empty;
    private string _browsePath = string.Empty;

    /// <summary>是否在"分类详情"里（面板内导航，不弹资源管理器）。</summary>
    public bool IsBrowsing
    {
        get => _isBrowsing;
        private set
        {
            if (SetProperty(ref _isBrowsing, value))
            {
                OnPropertyChanged(nameof(IsHome));
            }
        }
    }

    public bool IsHome => !_isBrowsing;

    public string BrowseTitle
    {
        get => _browseTitle;
        private set => SetProperty(ref _browseTitle, value);
    }

    public string BrowseSubtitle
    {
        get => _browseSubtitle;
        private set => SetProperty(ref _browseSubtitle, value);
    }

    public string BrowsePath => _browsePath;

    public ObservableCollection<BrowseItem> BrowseItems { get; } = new();

    /// <summary>点分类文件夹：面板内切换到文件列表页。</summary>
    public void OpenCategory(string directory, string title)
    {
        // 没给路径（或给了个空串）时退回 <归档根>/<分类名>
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(_settings.ArchiveRoot, title);
        }

        Section = "category";
        _browsePath = directory;
        BrowseTitle = title;
        BrowseItems.Clear();

        if (!Directory.Exists(directory))
        {
            BrowseSubtitle = "目录不存在（可能还没归档过文件）";
            IsBrowsing = true;
            return;
        }

        try
        {
            var files = new DirectoryInfo(directory)
                .EnumerateFiles()
                .Where(static f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(static f => f.LastWriteTime)
                .Take(500)
                .ToList();

            foreach (var file in files)
            {
                BrowseItems.Add(new BrowseItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    SizeText = BrowseItem.FormatSize(file.Length),
                    TimeText = file.LastWriteTime.ToString("MM-dd HH:mm"),
                    Icon = ShellIconProvider.ForFile(file.FullName),
                });
            }

            var folders = Directory.GetDirectories(directory).Length;
            BrowseSubtitle = $"{files.Count} 个文件" + (folders > 0 ? $" · {folders} 个子目录" : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BrowseSubtitle = "读取失败：" + ex.Message;
        }

        IsBrowsing = true;
    }

    public void CloseBrowse() => IsBrowsing = false;

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

            record.BatchId = NextBatchId(record.Category);
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
                _archiveLog.Archive(record.FileName, record.Category, "全局监听", record.SourcePath, record.ArchivedPath ?? string.Empty);
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

    /// <summary>重建左侧导航：一级为「应用与快捷方式 / 最近归档」，分类作为二级。</summary>
    public void RebuildNav()
    {
        RootNav.Clear();
        RootNav.Add(new NavEntry("应用与快捷方式", "\uE71D", "section", "shortcuts"));
        RootNav.Add(new NavEntry("最近归档", "\uE81C", "section", "recent"));
        RootNav.Add(new NavEntry("分类", "\uE8B7", "group", "categories", Badge: "\uE76C"));

        // 用户自定义的一级导航（指向任意文件夹）
        foreach (var item in _settings.UserNavItems)
        {
            if (!string.IsNullOrWhiteSpace(item.Path) && Directory.Exists(item.Path))
            {
                RootNav.Add(new NavEntry(item.Name, "\uE8B7", "user", item.Path));
            }
        }

        CategoryNav.Clear();
        foreach (var category in Categories)
        {
            CategoryNav.Add(new NavEntry(category.Title, string.Empty, "category", category.Path ?? string.Empty));
        }

        // ObservableCollection 自己会发变更通知，这里不用再手动通知
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

        record.BatchId = NextBatchId(record.Category);

        var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
        {
            ArchiveRoot = _settings.ArchiveRoot,
            MoveInsteadOfCopy = _settings.MoveInsteadOfCopy,
            UseContentClassification = _settings.ContentClassification,
        });

        _index.Upsert(outcome.Record);

        if (outcome.Success)
        {
            _archiveLog.Archive(record.FileName, record.Category, record.Source.DisplayName(), record.SourcePath, record.ArchivedPath ?? string.Empty);
        }

        RaiseConfirmed(outcome.Success ? $"已归档到「{record.Category}」" : "归档失败", outcome.Success);
        Log(outcome.Success
            ? $"已归档 {record.FileName} → {record.Category}" + (record.CategoryReason is null ? string.Empty : $"（{record.CategoryReason}）")
            : $"归档失败 {record.FileName}: {outcome.Error}");
        BuildRecent();
        BuildPending();
        BuildCategories();
    }

    /// <summary>待接收里已勾选的文件数量。</summary>
    /// <summary>操作完成通知（文字, 是否成功）：面板据此播放"确认动画"，不依赖日志文本。</summary>
    public event Action<string, bool>? Confirmed;

    private void RaiseConfirmed(string text, bool success = true) => Confirmed?.Invoke(text, success);

    public int PendingSelectedCount => PendingFiles.Count(static t => t.IsSelected);

    /// <summary>批量接收：挨个走和单条相同的归档流程。</summary>
    /// <summary>是否属于「快捷方式」类文件：这类拖进来只登记成面板快捷方式，不当文件归档。</summary>
    public static bool IsShortcutFile(string path) => DeskFlow.Core.Files.ShortcutKinds.IsShortcut(path);

    /// <summary>把 .lnk/.url 直接登记成面板快捷方式（不复制、不分类、不进归档目录）。</summary>
    public int AddShortcuts(IEnumerable<string> paths)
    {
        var added = DeskFlow.Infrastructure.Settings.ShortcutRegistry.Add(_settings, paths);
        if (added > 0)
        {
            BuildShortcuts();
            RaiseConfirmed($"已添加 {added} 个快捷方式");
            Log($"拖入 {added} 个快捷方式（.lnk 不归档，只放到面板）");
        }

        return added;
    }

    public async Task<int> AcceptSelectedPendingAsync()
    {
        var selected = PendingFiles.Where(static t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return 0;
        }

        var done = 0;
        foreach (var tile in selected)
        {
            await AcceptPendingAsync(tile);
            done++;
        }

        Log($"批量接收 {done} 个文件");
        return done;
    }

    /// <summary>一键忽略：把当前所有待接收一次性标记为忽略。</summary>
    public int IgnoreAllPending()
    {
        var pending = _index.Pending().ToList();
        foreach (var record in pending)
        {
            record.Status = ArchiveStatus.Ignored;
            _index.Upsert(record);
        }

        BuildPending();
        if (pending.Count > 0)
        {
            RaiseConfirmed($"已忽略 {pending.Count} 个文件");
            Log($"已忽略 {pending.Count} 个待接收文件");
        }

        return pending.Count;
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
        // 系统托盘通知：新文件进来时必须让用户看到（启动补捞的历史文件不弹，避免刷屏）
        {
            ToastService.Show($"收到新文件（{incoming.Source.DisplayName()}）", info.Name, incoming.FullPath);
        }
        BuildPending();
        BuildRecent();
    }

    private async Task ArchiveAndRecordAsync(FileRecord record)
    {
        record.BatchId = NextBatchId(record.Category);

        var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
        {
            ArchiveRoot = _settings.ArchiveRoot,
            MoveInsteadOfCopy = _settings.MoveInsteadOfCopy,
            UseContentClassification = _settings.ContentClassification,
        });

        var dispatcher = Application.Current?.Dispatcher;
        void Apply()
        {
            _index.Upsert(outcome.Record);

            if (outcome.Success)
            {
                _archiveLog.Archive(record.FileName, record.Category, record.Source.DisplayName(), record.SourcePath, record.ArchivedPath ?? string.Empty);
            }

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
    private PanelTile MakeTile(TileKind kind, string title, string subtitle, System.Windows.Media.ImageSource? icon, string? path = null, string? recordId = null)
    {
        var tile = new PanelTile
        {
            Title = title,
            Subtitle = subtitle,
            Icon = icon,
            Path = path,
            Kind = kind,
            RecordId = recordId,
        };

        ApplyGeometry(tile);
        return tile;
    }

    /// <summary>把当前密度写进一个格子里（新建和重排共用这一段）。</summary>
    private void ApplyGeometry(PanelTile tile)
    {
        tile.BoxWidth = TileWidth;
        tile.BoxHeight = TileHeight;
        tile.IconSize = IconSize;
        tile.TitleFontSize = TileFontSize;
        tile.SubtitleFontSize = SubtitleFontSize;
        tile.LineHeight = _lineHeight;
        tile.MaxTitleHeight = _maxTitleHeight;
        tile.ShowSubtitle = ShowSubtitle && !string.IsNullOrEmpty(tile.Subtitle);
        tile.TitleText = FitFileName(tile.Title, TileWidth - 4, TileFontSize, 2);
        tile.SubtitleText = FitText(tile.Subtitle, TileWidth - 4, SubtitleFontSize, 1);
    }

    /// <summary>宽度变了：原位改所有格子的尺寸与文字，不重建列表（重建会触发一堆集合变更）。</summary>
    private int RelayoutTiles()
    {
        var count = 0;
        foreach (var tile in SystemPlaces.Concat(Shortcuts).Concat(PendingFiles).Concat(RecentFiles).Concat(Categories))
        {
            ApplyGeometry(tile);
            count++;
        }

        return count;
    }

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

    /// <summary>给即将归档的文件分配批次号：同类别 + 15 秒内共用一批。</summary>
    private string NextBatchId(string category)
    {
        var now = DateTimeOffset.Now;
        if (_lastBatchId is not null &&
            string.Equals(_lastBatchCategory, category, StringComparison.OrdinalIgnoreCase) &&
            (now - _lastBatchAt).TotalSeconds <= 15)
        {
            _lastBatchAt = now;
            return _lastBatchId;
        }

        _lastBatchId = Guid.NewGuid().ToString("N")[..8];
        _lastBatchCategory = category;
        _lastBatchAt = now;
        return _lastBatchId;
    }

    /// <summary>把可撤销记录按批次分组（类别 + 批次号），用于"撤销最近一批"。</summary>
    public IReadOnlyList<DeskFlow.Infrastructure.History.ArchiveBatch> GetUndoableBatches() =>
        DeskFlow.Infrastructure.History.ArchiveBatches.Group(GetUndoableRecords());

    /// <summary>批量撤销一整批，返回成功数量。</summary>
    public async Task<int> UndoBatchAsync(DeskFlow.Infrastructure.History.ArchiveBatch batch)
    {
        var done = 0;
        foreach (var record in batch.Records)
        {
            if (await UndoAsync(record))
            {
                done++;
            }
        }

        return done;
    }

    /// <summary>
    /// 把手动拖进面板的文件归档到指定类别（categoryName 为空则按扩展名自动分类）。
    /// 默认复制；按住 Shift 拖拽 = 移动（和资源管理器一致）。返回成功数量。
    /// </summary>
    public async Task<int> ImportDroppedAsync(IReadOnlyList<string> paths, string? categoryName, bool move)
    {
        var archiveRoot = _settings.ArchiveRoot;
        Directory.CreateDirectory(archiveRoot);

        var files = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    files.Add(path);
                }
                else if (Directory.Exists(path))
                {
                    files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Take(50));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log("拖入的路径读不了：" + path);
            }
        }

        if (files.Count == 0)
        {
            Log("拖进来的是空列表或不能读的目录");
            return 0;
        }

        var done = 0;
        foreach (var file in files)
        {
            var record = new FileRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                SourcePath = file,
                FileName = Path.GetFileName(file),
                SizeBytes = new FileInfo(file).Length,
                Source = FileSource.Manual,
                Category = categoryName ?? "其他",
                DetectedAt = DateTimeOffset.Now,
                Status = ArchiveStatus.Pending,
            };

            var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
            {
                ArchiveRoot = archiveRoot,
                MoveInsteadOfCopy = move,
                CategoryOverride = categoryName,
                UseContentClassification = _settings.ContentClassification,
                UnrecognizedBucket = _settings.UnrecognizedBucket,
            });

            _index.Upsert(outcome.Record);

            if (outcome.Success)
            {
                done++;
                _archiveLog.Archive(record.FileName, record.Category, "拖入", record.SourcePath, record.ArchivedPath ?? string.Empty);
            }
            else
            {
                Log($"拖入失败 {record.FileName}: {outcome.Error}");
            }
        }

        BuildRecent();
        BuildCategories();

        var where = string.IsNullOrWhiteSpace(categoryName) ? "自动分类" : $"「{categoryName}」";
        ToastService.Show($"已收录 {done} 个文件", $"放进了 {where}（{(move ? "移动" : "复制")}）", archiveRoot);
        Log($"拖入归档 {done}/{files.Count} 个文件 → {where}");
        return done;
    }

    /// <summary>按记录 id 撤销（面板右键用）。</summary>
    public async Task<bool> UndoByIdAsync(string recordId)
    {
        var record = _index.All.FirstOrDefault(r => r.Id == recordId);
        return record is not null && await UndoAsync(record);
    }

    /// <summary>删除归档文件（进回收站），记录标记为已删除。</summary>
    /// <summary>把选中的归档文件删到回收站，并把对应记录标记为已删除。</summary>
    public int DeleteArchivedFiles(IEnumerable<string> paths)
    {
        var removed = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (!DeskFlow.Infrastructure.Services.RecycleBin.TryDelete(path))
            {
                Log($"删除失败 {Path.GetFileName(path)}");
                continue;
            }

            removed++;

            var record = _index.All.FirstOrDefault(r =>
                string.Equals(r.ArchivedPath, path, StringComparison.OrdinalIgnoreCase));
            if (record is not null)
            {
                record.Status = ArchiveStatus.Deleted;
                _index.Upsert(record);
            }
        }

        if (removed > 0)
        {
            RaiseConfirmed($"已删除 {removed} 个文件（回收站）");
            Log($"删除 {removed} 个归档文件（回收站）");
            if (IsBrowsing && !string.IsNullOrEmpty(_browsePath))
            {
                OpenCategory(_browsePath, BrowseTitle);
            }

            BuildRecent();
            BuildCategories();
        }

        return removed;
    }

    public bool DeleteById(string recordId)
    {
        var record = _index.All.FirstOrDefault(r => r.Id == recordId);
        if (record is null)
        {
            return false;
        }

        var path = record.ArchivedPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            record.Status = ArchiveStatus.Deleted;
            _index.Upsert(record);
            BuildRecent();
            BuildCategories();
            return false;
        }

        var ok = DeskFlow.Infrastructure.Services.RecycleBin.TryDelete(path);
        if (!ok)
        {
            Log($"删除失败 {record.FileName}");
            ToastService.Show("删除失败", record.FileName, path);
            return false;
        }

        record.Status = ArchiveStatus.Deleted;
        record.ArchivedPath = null;
        _index.Upsert(record);
        Log($"已删除归档文件 {record.FileName}（回收站）");
        ToastService.Show("已删除", $"{record.FileName} 已移到回收站", record.SourcePath);
        BuildRecent();
        BuildCategories();
        return true;
    }

    /// <summary>可撤销的归档记录（归档文件还在的），按时间倒序。</summary>
    public IReadOnlyList<FileRecord> GetUndoableRecords(string? keyword = null)
    {
        var records = _index.All
            .Where(static r => r.Status == ArchiveStatus.Archived && !string.IsNullOrWhiteSpace(r.ArchivedPath) && File.Exists(r.ArchivedPath))
            .OrderByDescending(static r => r.ArchivedAt ?? r.DetectedAt);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            records = records
                .Where(r => r.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                            (r.Category ?? string.Empty).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static r => r.ArchivedAt ?? r.DetectedAt);
        }

        return records.ToList();
    }

    /// <summary>撤销归档：文件搬回原位置，写日志，刷新面板列表。</summary>
    public async Task<bool> UndoAsync(FileRecord record)
    {
        var from = record.ArchivedPath ?? string.Empty;
        var outcome = await _archiver.UndoAsync(record);
        _index.Upsert(outcome.Record);

        if (!outcome.Success)
        {
            Log($"撤销失败 {record.FileName}: {outcome.Error}");
            ToastService.Show("撤销失败", $"{record.FileName}：{outcome.Error}", record.SourcePath);
            return false;
        }

        _archiveLog.Undo(record.FileName, record.Category, record.Source.DisplayName(), from, record.RestoredPath ?? record.SourcePath);
            RaiseConfirmed("已撤销，文件已回原位置");
        Log($"已撤销归档 {record.FileName} → {record.RestoredPath}");
        ToastService.Show("已撤销归档", $"{record.FileName} 已放回 {Path.GetDirectoryName(record.RestoredPath)}", record.RestoredPath);

        BuildRecent();
        BuildCategories();
        return true;
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

            record.BatchId = NextBatchId(record.Category);

            var outcome = await _archiver.ArchiveAsync(record, new ArchiveOptions
            {
                ArchiveRoot = _settings.ArchiveRoot,
                MoveInsteadOfCopy = _settings.MoveInsteadOfCopy,
                UseContentClassification = _settings.ContentClassification,
                UnrecognizedBucket = _settings.UnrecognizedBucket,
            });

            _index.Upsert(outcome.Record);
            if (outcome.Success)
            {
                archived++;
                _archiveLog.Archive(record.FileName, record.Category, "手动添加", record.SourcePath, record.ArchivedPath ?? string.Empty);
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
        // 归档目录单独一组，永远显示（以前混在快捷方式里，用户一加自己的快捷方式就没了）
        SystemPlaces.Clear();
        SystemPlaces.Add(MakeTile(
            TileKind.Category,
            "归档目录",
            "文件夹",
            ShellIconProvider.ForFolder(_settings.ArchiveRoot),
            _settings.ArchiveRoot));

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
                if (string.Equals(seed.Name, "归档目录", StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // 已由 SystemPlaces 固定展示
                }

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
        RebuildCollapsedTiles();
    }

    private void RebuildCollapsedTiles()
    {
        CollapsedTiles.Clear();
        foreach (var tile in SystemPlaces.Concat(Shortcuts))
        {
            CollapsedTiles.Add(tile);
        }
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
                record.DisplayPath,
                record.Id));
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

        var archiveRoot = _settings.ArchiveRoot;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = _index.All.Where(static r => r.Status == ArchiveStatus.Archived).ToList();

        // 1) 分类规则里的类别
        foreach (var category in _categories.All)
        {
            var directory = Path.Combine(archiveRoot, FileArchiver.SanitizeFolderName(category.Name));
            var count = records.Count(r => string.Equals(r.Category, category.Name, StringComparison.OrdinalIgnoreCase));
            known.Add(category.Name);

            if (count == 0 && !Directory.Exists(directory))
            {
                continue;
            }

            Categories.Add(MakeTile(
                TileKind.Browse,
                category.Name,
                $"{count} 个",
                ShellIconProvider.ForFolder(directory),
                directory));
        }

        // 2) 归档目录里实际存在的文件夹（"按内容自动分类"会现场建目录，必须能在二级菜单看到）
        try
        {
            if (Directory.Exists(archiveRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(archiveRoot)
                             .OrderBy(static d => Path.GetFileName(d), StringComparer.CurrentCulture))
                {
                    var name = Path.GetFileName(directory);
                    if (known.Contains(name))
                    {
                        continue;
                    }

                    int count;
                    try
                    {
                        count = Directory.EnumerateFiles(directory).Count();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    Categories.Add(MakeTile(
                        TileKind.Browse,
                        name,
                        $"{count} 个",
                        ShellIconProvider.ForFolder(directory),
                        directory));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("扫描归档目录失败：" + ex.Message);
        }

        RebuildNav();
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