using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media;
using System.Windows.Threading;
using DeskFlow.Infrastructure.Services;

namespace DeskFlow.App.Services;

/// <summary>右下角弹出的提示卡片：显示"发现新文件 / 已归档"，点击直接打开文件。</summary>
public partial class ToastWindow : Window
{
    private readonly string? _path;
    private readonly DispatcherTimer _timer;

    public ToastWindow(string title, string detail, string? path)
    {
        InitializeComponent();
        _path = path;

        TitleText.Text = title;
        DetailText.Text = detail;
        IconImage.Source = path is null ? null : ShellIconProvider.ForFile(path);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();

        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 布局没跑完时 ActualWidth/ActualHeight 还是 0，直接用会把窗口摆到屏幕外
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : 120;

        var screen = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? System.Drawing.Rectangle.Empty;
        var dpi = VisualTreeHelper.GetDpi(this);
        Diagnostics.Log($"toast layout: systemWorkArea={area.Right}x{area.Bottom} screen={screen.Width}x{screen.Height} dpi={dpi.DpiScaleX:F2} actual={ActualWidth:F0}x{ActualHeight:F0}");

        Left = area.Right - width - 18;
        Top = area.Bottom - height - 18;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_path))
        {
            ShellLauncher.Open(_path);
        }

        Close();
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}