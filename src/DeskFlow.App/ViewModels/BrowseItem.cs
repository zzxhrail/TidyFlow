using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DeskFlow.App.ViewModels;

/// <summary>分类详情页里的一行文件。</summary>
public sealed class BrowseItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public string SizeText { get; init; } = string.Empty;

    public string TimeText { get; init; } = string.Empty;

    public ImageSource? Icon { get; init; }

    /// <summary>多选状态（勾选框）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
