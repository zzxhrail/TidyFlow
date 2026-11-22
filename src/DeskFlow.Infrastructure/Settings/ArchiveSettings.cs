using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Settings;

/// <summary>面板上的一个快捷入口。</summary>
public sealed class ShortcutItem
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsFolder { get; set; }
}

/// <summary>
/// 归档相关配置，落在 settings.json。
/// 关键开关：每种来源的"自动下载"—— 勾上 = 发现即归档；不勾 = 只进"待接收"列表等用户点。
/// </summary>
public sealed class ArchiveSettings
{
    private readonly ISettingsProxy _store;

    public ArchiveSettings(ISettingsProxy? store = null)
    {
        _store = store ?? new JsonSettingsProxy();
        ArchiveRoot = _store.Get("archive.root", Path.Combine(AppPaths.DefaultOutputDirectory, "archive")) ?? Path.Combine(AppPaths.DefaultOutputDirectory, "archive");
        MoveInsteadOfCopy = _store.Get("archive.move", false);
        AutoDownload = _store.Get("archive.autodownload", new Dictionary<string, bool>()) ?? new Dictionary<string, bool>();
        ExtraTargets = _store.Get("watch.extra", new List<ShortcutItem>()) ?? new List<ShortcutItem>();
        Shortcuts = _store.Get("panel.shortcuts", new List<ShortcutItem>()) ?? new List<ShortcutItem>();
        UserCategories = _store.Get("category.user", new List<string[]>()) ?? new List<string[]>();
        PanelX = _store.Get("panel.x", -1);
        PanelY = _store.Get("panel.y", -1);
        PanelWidth = _store.Get("panel.width", 0);
        PanelHeight = _store.Get("panel.height", 0);
        PanelLayoutMigrated = _store.Get("panel.layout.v2", false);
        ContentClassification = _store.Get("archive.content-ai", true);
        SpacePreviewEnabled = _store.Get("panel.space-preview", true);
        UnrecognizedBucket = _store.Get("archive.unrecognized-bucket", true);
        AnimationsEnabled = _store.Get("ui.animations", true);
        UserNavItems = _store.Get("nav.user", new List<ShortcutItem>());
        PanelPinned = _store.Get("panel.pinned", false);
        DiscoveryRoots = _store.Get("discovery.roots", new List<string>()) ?? new List<string>();
        GlobalWatchEnabled = _store.Get("watch.global.enabled", false);
        GlobalRoots = _store.Get("watch.global.roots", new List<string>()) ?? new List<string>();
        GlobalExtensions = _store.Get("watch.global.extensions", new List<string>()) ?? new List<string>();
    }

    public string ArchiveRoot { get; set; }

    public bool MoveInsteadOfCopy { get; set; }

    /// <summary>key = FileSource.Key()（qq / wechat / dingtalk ...）。</summary>
    public Dictionary<string, bool> AutoDownload { get; set; }

    public List<ShortcutItem> ExtraTargets { get; set; }

    public List<ShortcutItem> Shortcuts { get; set; }

    /// <summary>每条 = [类别名, 扩展名...]，读出来还原成 CategoryDefinition。</summary>
    public List<string[]> UserCategories { get; set; }

    public double PanelX { get; set; }

    public double PanelY { get; set; }

    /// <summary>用户拖出来的面板宽高；0 表示还没拖过，交给程序按内容自适应一次。</summary>
    public double PanelWidth { get; set; }

    public double PanelHeight { get; set; }

    /// <summary>是否已按 0.9 的新默认尺寸（更宽的「导航 + 内容」布局）迁移过。</summary>
    public bool PanelLayoutMigrated { get; set; }

    /// <summary>是否按文件内容（读正文关键词）自动分类，而不是只看扩展名。</summary>
    public bool ContentClassification { get; set; } = true;

    /// <summary>悬停文件后按空格快速预览（类似 macOS 的 Quick Look）。</summary>
    public bool SpacePreviewEnabled { get; set; } = true;

    /// <summary>内容分类判不出来时，文档类文件进「未识别」，而不是退回扩展名类别。</summary>
    public bool UnrecognizedBucket { get; set; } = true;

    /// <summary>界面动画（淡入 / 滑入 / 勾选框弹出）。关掉 = 全部瞬切。</summary>
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>用户自定义的一级导航项（指向任意文件夹）。</summary>
    public List<ShortcutItem> UserNavItems { get; set; } = [];

    /// <summary>是否"顶住"：顶住后面板不能被拖动（位置锁死），但点按交互照常。</summary>
    public bool PanelPinned { get; set; }

    /// <summary>兜底扫描找到的客户端数据根目录（深盘、改过保存路径的情况）。</summary>
    public List<string> DiscoveryRoots { get; set; }

    /// <summary>全局监听：整机任意目录落盘即收（按扩展名筛选）。</summary>
    public bool GlobalWatchEnabled { get; set; }

    public List<string> GlobalRoots { get; set; }

    public List<string> GlobalExtensions { get; set; }

    public bool IsAutoDownload(FileSource source) =>
        AutoDownload.TryGetValue(source.Key(), out var value) && value;

    public void SetAutoDownload(FileSource source, bool enabled)
    {
        AutoDownload[source.Key()] = enabled;
        Save();
    }

    public void AddUserCategory(string name, IEnumerable<string> extensions)
    {
        UserCategories.RemoveAll(c => c.Length > 0 && string.Equals(c[0], name, StringComparison.OrdinalIgnoreCase));
        UserCategories.Add([name, .. extensions.Select(e => e.StartsWith('.') ? e : "." + e)]);
        Save();
    }

    public void RemoveUserCategory(string name)
    {
        UserCategories.RemoveAll(c => c.Length > 0 && string.Equals(c[0], name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void Save()
    {
        _store.Set("archive.root", ArchiveRoot);
        _store.Set("archive.move", MoveInsteadOfCopy);
        _store.Set("archive.autodownload", AutoDownload);
        _store.Set("watch.extra", ExtraTargets);
        _store.Set("panel.shortcuts", Shortcuts);
        _store.Set("category.user", UserCategories);
        _store.Set("panel.x", PanelX);
        _store.Set("panel.y", PanelY);
        _store.Set("panel.width", PanelWidth);
        _store.Set("panel.height", PanelHeight);
        _store.Set("panel.layout.v2", PanelLayoutMigrated);
        _store.Set("archive.content-ai", ContentClassification);
        _store.Set("panel.space-preview", SpacePreviewEnabled);
        _store.Set("archive.unrecognized-bucket", UnrecognizedBucket);
        _store.Set("ui.animations", AnimationsEnabled);
        _store.Set("nav.user", UserNavItems);
        _store.Set("panel.pinned", PanelPinned);
        _store.Set("discovery.roots", DiscoveryRoots);
        _store.Set("watch.global.enabled", GlobalWatchEnabled);
        _store.Set("watch.global.roots", GlobalRoots);
        _store.Set("watch.global.extensions", GlobalExtensions);
        _store.Flush();
    }
}

/// <summary>ArchiveSettings 只依赖这一层，方便以后换注册表/数据库。</summary>
public interface ISettingsProxy
{
    T? Get<T>(string key, T? fallback = default);

    void Set<T>(string key, T value);

    void Flush();
}

internal sealed class JsonSettingsProxy : ISettingsProxy
{
    private readonly Core.Abstractions.ISettingsStore _store = new JsonSettingsStore();

    public T? Get<T>(string key, T? fallback = default) => _store.Get(key, fallback);

    public void Set<T>(string key, T value) => _store.Set(key, value);

    public void Flush()
    {
    }
}