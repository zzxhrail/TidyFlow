using System.IO;
using System.Reflection;
using System.Windows;
using DeskFlow.Deployment;
using Microsoft.Win32;

namespace DeskFlow.App.Install;

/// <summary>安装界面：可选安装位置、开机自启、快捷方式。</summary>
public partial class InstallerWindow : Window
{
    private readonly SetupOptions _options;
    private bool _installed;
    private string _installedDirectory = string.Empty;

    public InstallerWindow(SetupOptions options)
    {
        InitializeComponent();
        _options = options;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = $"版本 {version} · 仅安装到当前用户，无需管理员权限";
        PathBox.Text = string.IsNullOrWhiteSpace(options.Directory)
            ? InstallLayout.DefaultInstallDirectory
            : options.Directory;

        AutostartCheck.IsChecked = options.Autostart;
        DesktopCheck.IsChecked = options.Shortcuts;
        StartMenuCheck.IsChecked = options.Shortcuts;

        var existing = InstallLayout.GetInstalledDirectory();
        if (existing is not null)
        {
            ExistingBox.Visibility = Visibility.Visible;
            ExistingText.Text = $"检测到已安装：{existing}（版本 {InstallLayout.GetInstalledVersion() ?? "未知"}）。" +
                                "继续安装会覆盖程序文件，配置与归档记录保留。";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择安装位置", InitialDirectory = SafeInitialDirectory() };
        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = Path.Combine(dialog.FolderName, InstallLayout.ProductName);
        }
    }

    private string SafeInitialDirectory()
    {
        try
        {
            var candidate = Path.GetDirectoryName(PathBox.Text.Trim());
            return Directory.Exists(candidate) ? candidate! : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch (ArgumentException)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            LaunchInstalled();
            return;
        }

        var directory = PathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("请先选择安装位置。", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var effective = new SetupOptions(
            Silent: false,
            Directory: directory,
            Autostart: AutostartCheck.IsChecked == true,
            Shortcuts: DesktopCheck.IsChecked == true || StartMenuCheck.IsChecked == true,
            KeepUserData: true);

        ToggleBusy(true);
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;

        var progress = new Progress<(string Message, int Percent)>(tuple =>
        {
            StatusText.Text = tuple.Message;
            Progress.Value = tuple.Percent;
        });

        var result = await Task.Run(() => InstallerService.Install(effective, (message, percent) =>
            ((IProgress<(string, int)>)progress).Report((message, percent))));

        ToggleBusy(false);

        if (!result.Success)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "安装失败：" + result.Message;
            return;
        }

        _installed = true;
        _installedDirectory = result.Message;
        Progress.Value = 100;
        StatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        StatusText.Text = $"安装完成：{_installedDirectory}";
        HintText.Text = "已建好快捷方式与卸载项；「设置 → 应用」里可以看到 DeskFlow，可随时卸载。";
        FooterHint.Text = "也可以直接关掉这个窗口";
        InstallButton.Content = "启动 DeskFlow";
        CancelButton.Content = "关闭";
        AutostartCheck.IsEnabled = false;
        DesktopCheck.IsEnabled = false;
        StartMenuCheck.IsEnabled = false;
        PathBox.IsEnabled = false;
    }

    private void LaunchInstalled()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(InstallLayout.ExePath(_installedDirectory))
            {
                UseShellExecute = true,
                WorkingDirectory = _installedDirectory,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败：" + ex.Message, "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Close();
    }

    private void ToggleBusy(bool busy)
    {
        InstallButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        AutostartCheck.IsEnabled = !busy;
        DesktopCheck.IsEnabled = !busy;
        StartMenuCheck.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
