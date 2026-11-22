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
                    if (info.LastWriteTime < since || _index.KnowsSourcePath(file))
                    {
                        continue;
                    }

                    HandleDetected(new IncomingFile(file, target.Source, DateTimeOffset.Now));
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

        _settings.SetAutoDownload(source, enabled);
        Log($"{toggle.Name} 自动下载 = {(enabled ? "开" : "关")}");
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
        list.AddRange(SourceDirectoryResolver.Discover());
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

    private void BuildSourceToggles()
    {
        Sources.Clear();
        var targets = SourceDirectoryResolver.Discover();
        foreach (var source in new[] { FileSource.Qq, FileSource.WeChat, FileSource.DingTalk, FileSource.Downloads })
        {
            var matched = targets.Where(t => t.Source == source).ToList();
            var detail = matched.Count == 0
                ? "未检测到目录"
                : matched.Count == 1 ? matched[0].Directory : $"{matched.Count} 个目录";
            Sources.Add(new SourceToggle(source.DisplayName(), detail, source.ToString(), matched.Count > 0, _settings.IsAutoDownload(source)));
        }
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

            Shortcuts.Add(new PanelTile
            {
                Title = item.Name,
                Subtitle = item.IsFolder ? "文件夹" : "应用",
                Icon = ShellIconProvider.ForPath(item.Path),
                Path = item.Path,
                Kind = item.IsFolder ? TileKind.Category : TileKind.Shortcut,
            });
        }

        Shortcuts.Add(new PanelTile
        {
            Title = "添加",
            Subtitle = "快捷方式",
            Icon = ShellIconProvider.ForFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            Kind = TileKind.Add,
        });

        if (Shortcuts.Count <= 1)
        {
            foreach (var seed in ShortcutSeeder.Seed())
            {
                Shortcuts.Add(new PanelTile
                {
                    Title = seed.Name,
                    Subtitle = seed.IsFolder ? "文件夹" : "应用",
                    Icon = ShellIconProvider.ForPath(seed.Path),
                    Path = seed.Path,
                    Kind = seed.IsFolder ? TileKind.Category : TileKind.Shortcut,
                });
            }
        }
    }

    private void BuildRecent()
    {
        RecentFiles.Clear();
        foreach (var record in _index.Recent(8))
        {
            RecentFiles.Add(new PanelTile
            {
                Title = record.FileName,
                Subtitle = $"{record.Category} · {record.Source.DisplayName()}",
                Icon = ShellIconProvider.ForFile(record.ArchivedPath ?? record.SourcePath),
                Path = record.DisplayPath,
                Kind = TileKind.File,
            });
        }

        HasRecent = RecentFiles.Count > 0;
        OnPropertyChanged(nameof(HasRecent));
    }

    private void BuildPending()
    {
        // 待接收清空后 HasPending 需要刷新

        PendingFiles.Clear();
        foreach (var record in _index.Pending().Take(8))
        {
            PendingFiles.Add(new PanelTile
            {
                Title = record.FileName,
                Subtitle = $"{record.Source.DisplayName()} · 点我归档",
                Icon = ShellIconProvider.ForFile(record.SourcePath),
                Path = record.SourcePath,
                Kind = TileKind.Pending,
                RecordId = record.Id,
            });
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

            Categories.Add(new PanelTile
            {
                Title = category.Name,
                Subtitle = $"{count} 个",
                Icon = ShellIconProvider.ForFolder(directory),
                Path = directory,
                Kind = TileKind.Category,
            });
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