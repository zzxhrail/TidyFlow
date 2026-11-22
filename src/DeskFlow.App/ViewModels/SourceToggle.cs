namespace DeskFlow.App.ViewModels;

/// <summary>来源开关：显示聊天工具是否检测到目录 + 该来源是否"自动下载"。</summary>
public sealed class SourceToggle : ObservableObject
{
    private bool _autoDownload;

    public SourceToggle(string name, string detail, string sourceKey, bool detected, bool autoDownload)
    {
        Name = name;
        Detail = detail;
        SourceKey = sourceKey;
        Detected = detected;
        _autoDownload = autoDownload;
    }

    public string Name { get; }

    public string Detail { get; }

    public string SourceKey { get; }

    public bool Detected { get; }

    public bool IsAvailable => Detected;

    public bool AutoDownload
    {
        get => _autoDownload;
        set => SetProperty(ref _autoDownload, value);
    }
}
