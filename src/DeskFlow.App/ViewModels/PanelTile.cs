using System.ComponentModel;
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

/// <summary>
/// 面板上的一个图标格子。图标由 ShellIconProvider 从系统里取，跟资源管理器一致。
/// 尺寸与字号是可变属性并带通知：面板拉宽拉窄时**原位改值**，
/// 而不是把整个列表清空重建（重建会触发大量集合变更，拖拽缩放时肉眼可见地卡）。
/// </summary>
public sealed class PanelTile : INotifyPropertyChanged
{
    private double _boxWidth = 70;
    private double _boxHeight = 88;
    private double _iconSize = 30;
    private double _titleFontSize = 10.5;
    private double _subtitleFontSize = 9;
    private double _lineHeight = 13;
    private double _maxTitleHeight = 27;
    private bool _showSubtitle = true;
    private bool _isSelected;
    private string _titleText = string.Empty;
    private string _subtitleText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Title { get; init; }

    public string Subtitle { get; init; } = string.Empty;

    public ImageSource? Icon { get; init; }

    public string? Path { get; init; }

    public required TileKind Kind { get; init; }

    /// <summary>Pending 时对应的记录 id。</summary>
    public string? RecordId { get; init; }

    public double BoxWidth
    {
        get => _boxWidth;
        set => Set(ref _boxWidth, value);
    }

    public double BoxHeight
    {
        get => _boxHeight;
        set => Set(ref _boxHeight, value);
    }

    public double IconSize
    {
        get => _iconSize;
        set => Set(ref _iconSize, value);
    }

    public double TitleFontSize
    {
        get => _titleFontSize;
        set => Set(ref _titleFontSize, value);
    }

    public double SubtitleFontSize
    {
        get => _subtitleFontSize;
        set => Set(ref _subtitleFontSize, value);
    }

    public double LineHeight
    {
        get => _lineHeight;
        set => Set(ref _lineHeight, value);
    }

    public double MaxTitleHeight
    {
        get => _maxTitleHeight;
        set => Set(ref _maxTitleHeight, value);
    }

    public bool ShowSubtitle
    {
        get => _showSubtitle;
        set => Set(ref _showSubtitle, value);
    }

    /// <summary>按格子宽度算过字数的显示文本（中文按全角算），避免 WPF 里出现"字被切一半"。</summary>
    public string TitleText
    {
        get => _titleText;
        set => Set(ref _titleText, value);
    }

    public string SubtitleText
    {
        get => _subtitleText;
        set => Set(ref _subtitleText, value);
    }

    /// <summary>待接收列表里的多选状态（只有待接收格子用）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>是否待接收：模板据此显示左上角勾选框。</summary>
    public bool IsPending => Kind == TileKind.Pending;

    /// <summary>悬停显示完整信息：文件名 + 路径。</summary>
    public string ToolTip => string.IsNullOrWhiteSpace(Path) ? $"{Title}\n{Subtitle}" : $"{Title}\n{Path}";

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
