using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskFlow.App.Services;
using DeskFlow.App.ViewModels;
using DeskFlow.Infrastructure.Services;

namespace DeskFlow.App.Preview;

/// <summary>
/// 待接收文件的预览：图片直接看、文本前 200KB 直接读，
/// 其它格式给文件信息 + 用默认程序打开。窗口里就能归档 / 忽略，省得来回切。
/// </summary>
public partial class PreviewWindow : Window
{
    private const int MaxTextBytes = 200 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".yml", ".yaml", ".ini", ".cfg",
        ".cs", ".xaml", ".js", ".ts", ".py", ".java", ".c", ".cpp", ".h", ".html", ".css",
        ".sql", ".bat", ".ps1", ".sh", ".cmd", ".gitignore", ".env",
    };

    private readonly PanelViewModel _viewModel;
    private readonly PanelTile _tile;
    private readonly string _path;
    private readonly bool _isPending;

    public PreviewWindow(PanelViewModel viewModel, PanelTile tile, bool isPending = true)
    {
        _isPending = isPending;
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));

        _viewModel = viewModel;
        _tile = tile;
        _path = tile.Path ?? string.Empty;

        Title = "预览 · " + tile.Title;
        NameText.Text = tile.Title;
        PathText.Text = _path;
        FileIcon.Source = tile.Icon;

        if (!isPending)
        {
            // 已归档的文件：只保留"打开"和"关闭"，归档/忽略没有意义
            AcceptButton.Visibility = Visibility.Collapsed;
            IgnoreButton.Visibility = Visibility.Collapsed;
            OpenButton.Content = "用默认程序打开";
        }

        LoadPreview();
    }

    private void LoadPreview()
    {
        if (!File.Exists(_path))
        {
            MetaText.Text = "文件已不存在";
            ShowFallback("找不到这个文件", "可能已经被移走或删除。");
            return;
        }

        var info = new FileInfo(_path);
        MetaText.Text = $"{BrowseItem.FormatSize(info.Length)} · 修改于 {info.LastWriteTime:MM-dd HH:mm}";

        var extension = Path.GetExtension(_path);
        if (ImageExtensions.Contains(extension))
        {
            ShowImage();
        }
        else if (TextExtensions.Contains(extension))
        {
            ShowText();
        }
        else
        {
            ShowFallback("这个格式不能内嵌预览", "点下面的「用默认程序打开」查看内容；也可以直接「立即归档」收进分类。");
        }
    }

    private void ShowImage()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // 读到内存，别锁住原文件
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(_path, UriKind.Absolute);
            image.EndInit();

            ImagePreview.Source = image;
            ImagePreview.Visibility = Visibility.Visible;
            StatusText.Text = $"{image.PixelWidth} × {image.PixelHeight} 像素";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            ShowFallback("图片读不出来", ex.Message);
        }
    }

    private void ShowText()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            var length = (int)Math.Min(stream.Length, MaxTextBytes);
            var buffer = new byte[length];
            stream.ReadExactly(buffer, 0, length);

            var text = new StreamReader(new MemoryStream(buffer), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
                .ReadToEnd();

            TextPreview.Text = text;
            TextScroll.Visibility = Visibility.Visible;
            StatusText.Text = stream.Length > MaxTextBytes ? "只显示前 200 KB" : $"{length} 字节";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowFallback("文本读不出来", ex.Message);
        }
    }

    private void ShowFallback(string title, string hint)
    {
        FallbackTitle.Text = title;
        FallbackHint.Text = hint;
        FallbackPanel.Visibility = Visibility.Visible;
        StatusText.Text = string.Empty;
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        AcceptButton.Content = "归档中…";
        AcceptButton.IsEnabled = false;

        await _viewModel.AcceptPendingAsync(_tile);
        Close();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IgnorePending(_tile);
        Close();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellLauncher.Open(_path))
        {
            StatusText.Text = "打不开，可能没有关联程序";
        }
    }
}
