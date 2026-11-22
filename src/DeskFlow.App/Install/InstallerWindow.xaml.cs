using DeskFlow.App.Services;
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
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/icon-128.png"));
        HeaderLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon-48.png"));
        SourceInitialized += (_, _) => WindowEffects.Apply(this, BackdropKind.None);
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
            MessageBox.Show("请先选择安装位置。", "TidyFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var effective = new SetupOptions(
            Silent: false,
            Directory: directory,
            Autostart: AutostartCheck.IsChecked == true,
            Shortcuts: DesktopCheck.IsChecked == true || StartMenuCheck.IsChecked == true,
            KeepUserData: true);

        ToggleBusy(true);
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        StatusText.Text = "准备安装…";
        PercentText.Text = "0%";
        Progress.Value = 0;
        Progress.IsIndeterminate = true;

        var progress = new Progress<(string Message, int Percent)>(tuple =>
        {
            StatusText.Text = tuple.Message;
            PercentText.Text = tuple.Percent + "%";
            if (tuple.Percent > 0)
            {
                Progress.IsIndeterminate = false;

                // 让进度条"滑"到新值，而不是一格格跳（视觉上就是那种滚动前进）
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = tuple.Percent,
                    Duration = TimeSpan.FromMilliseconds(260),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                    },
                };

                Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
            }

            UpdateSteps(tuple.Percent);
        });

        var startedAt = DateTime.UtcNow;
        DeploymentResult result;
        try
        {
            result = await Task.Run(() => InstallerService.Install(effective, (message, percent) =>
                ((IProgress<(string, int)>)progress).Report((message, percent))));
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接结束进程（"点开始安装就闪退"），这里必须兜住
            ToggleBusy(false);
            Progress.IsIndeterminate = false;
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "安装失败：" + ex.GetType().Name + " — " + ex.Message;
            PercentText.Text = string.Empty;
            return;
        }

        // 机器快的时候 66 MB 复制不到 200ms 就完了，进度条一闪而过；
        // 这里给它一个最短 1.2 秒的可见时长（真实结果早就返回了，只是让过程看得见）
        var elapsed = (DateTime.UtcNow - startedAt).TotalMilliseconds;
        const double minimumVisibleMs = 1200;
        if (result.Success && elapsed < minimumVisibleMs)
        {
            Progress.IsIndeterminate = false;
            var remaining = minimumVisibleMs - elapsed;
            var from = Math.Max(Progress.Value, 60);
            Progress.BeginAnimation(
                System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new System.Windows.Media.Animation.DoubleAnimation(from, 100, TimeSpan.FromMilliseconds(remaining)));

            StatusText.Text = "收尾：写入快捷方式与卸载项…";
            PercentText.Text = "99%";
            UpdateSteps(92);   // 让最后一步显示"进行中"，别全都提前打勾
            await Task.Delay((int)remaining);
        }

        ToggleBusy(false);

        if (!result.Success)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "安装失败：" + result.Message;
            return;
        }

        _installed = true;
        _installedDirectory = result.Message;
        Progress.IsIndeterminate = false;
        Progress.Value = 100;
        PercentText.Text = "100%";
        UpdateSteps(100);
        StatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        StatusText.Text = $"安装完成：{_installedDirectory}";
        HintText.Text = "已建好快捷方式与卸载项；「设置 → 应用」里可以看到 DeskFlow，可随时卸载。";
        FooterHint.Text = "也可以直接关掉这个窗口";
        InstallButton.Content = "启动 TidyFlow";
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

    /// <summary>按百分比点亮步骤：完成打勾、当前加粗。</summary>
    private void UpdateSteps(int percent)
    {
        var steps = new[] { (1, Step1, 5), (2, Step2, 20), (3, Step3, 75), (4, Step4, 92) };
        foreach (var (_, block, threshold) in steps)
        {
            var text = block.Text[2..];   // 去掉开头的 ○/●/✓
            block.Text = percent >= Math.Min(threshold + 8, 100) ? "✓  " + text : percent >= threshold ? "●  " + text : "○  " + text;
            block.Foreground = percent >= threshold
                ? (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
                : (System.Windows.Media.Brush)FindResource("TextTertiaryBrush");
        }
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
