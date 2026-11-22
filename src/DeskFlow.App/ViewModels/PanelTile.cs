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

    public string ToolTip => string.IsNullOrWhiteSpace(Path) ? Subtitle : Path;
}
