using System.Windows.Media;

namespace DeskFlow.App.ViewModels;

public enum TileKind
{
    Shortcut = 0,
    File = 1,
    Category = 2,
    Pending = 3,
    Add = 4,
    Batch = 5,
    Settings = 6,

    /// <summary>分类文件夹：点开进面板内的列表页，而不是弹资源管理器。</summary>
    Browse = 7,
}

/// <summary>面板上的一个图标格子。图标由 ShellIconProvider 从系统里取，跟资源管理器一致。</summary>
public sealed class PanelTile
{
    public required string Title { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    public ImageSource? Icon { get; init; }

    public string? Path { get; init; }

    public required TileKind Kind { get; init; }

    /// <summary>Pending 时对应的记录 id。</summary>
    public string? RecordId { get; init; }

    // 由 PanelViewModel 在算完密度后写进来：格子尺寸、图标大小、字号，
    // 这样模板不依赖任何祖先元素绑定，缩放时行为可预测。
    public double BoxWidth { get; set; } = 70;

    public double BoxHeight { get; set; } = 88;

    public double IconSize { get; set; } = 30;

    public double TitleFontSize { get; set; } = 10.5;

    public double SubtitleFontSize { get; set; } = 9;

    public double LineHeight { get; set; } = 13;

    public double MaxTitleHeight { get; set; } = 27;

    public bool ShowSubtitle { get; set; } = true;

    /// <summary>按格子宽度算过字数的显示文本（中文按全角算），避免 WPF 里出现"字被切一半"。</summary>
    public string TitleText { get; set; } = string.Empty;

    public string SubtitleText { get; set; } = string.Empty;

    /// <summary>悬停显示完整信息：文件名 + 路径。</summary>
    public string ToolTip => string.IsNullOrWhiteSpace(Path) ? $"{Title}\n{Subtitle}" : $"{Title}\n{Path}";
}