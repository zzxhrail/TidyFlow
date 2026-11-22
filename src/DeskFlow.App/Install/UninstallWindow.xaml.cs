using DeskFlow.App.Services;
using System.IO;
using System.Reflection;
using System.Windows;
using DeskFlow.Deployment;

namespace DeskFlow.App.Install;

/// <summary>卸载界面：显示安装位置与占用，可选择是否保留配置与索引。</summary>
public partial class UninstallWindow : Window
{
    private readonly SetupOptions _options;
    private bool _done;

    public UninstallWindow(SetupOptions options)
    {
        InitializeComponent();

        Loaded += (_, _) => Services.Animations.FadeSlideIn(this, offsetY: 6, milliseconds: 160);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));
        HeaderLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon-48.png"));
        SourceInitialized += (_, _) => WindowEffects.Apply(this, BackdropKind.None);
        _options = options;
        KeepDataCheck.IsChecked = options.KeepUserData;

        var exe = Environment.ProcessPath;
        var directory = exe is null ? "—" : Path.GetDirectoryName(exe) ?? "—";
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—";

        LocationText.Text = $"安装位置：{directory}（版本 {version}）";
        var sizeKb = Directory.Exists(directory) ? InstallLayout.DirectorySizeKb(directory) : 0;
        SizeText.Text = $"占用空间：约 {Math.Max(1, sizeKb / 1024)} MB";
        SubtitleText.Text = "移除程序、快捷方式与开机自启";
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }

        var confirm = MessageBox.Show(
            "确定要卸载 TidyFlow 吗？\n\n归档目录里的文件和你的文档不会被删除。",
            "卸载 TidyFlow",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        UninstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;

        var keep = KeepDataCheck.IsChecked == true;
        var result = await Task.Run(() => InstallerService.Uninstall(keep));

        if (!result.Success)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "卸载失败：" + result.Message;
            UninstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            return;
        }

        _done = true;
        Progress.Value = 100;
        StatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        StatusText.Text = keep
            ? "已卸载。配置与归档索引保留在 %LOCALAPPDATA%\\DeskFlow，重装后会自动接上。"
            : "已卸载。配置、日志与索引已删除；归档目录里的文件仍然保留。";
        SubtitleText.Text = "卸载完成";
        UninstallButton.Content = "关闭";
        UninstallButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
