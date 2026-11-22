using System.IO;
using System.Windows;
using DeskFlow.App.Cli;
using DeskFlow.App.Install;
using DeskFlow.App.ViewModels;
using DeskFlow.Core.Categorization;
using DeskFlow.Core.Models;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Index;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App;

/// <summary>
/// 组合根 + 双入口：
///   DeskFlow.exe             → 桌面面板（常驻）
///   DeskFlow.exe --run ...   → 无界面批处理（计划任务/脚本）
/// 面板是主窗口；设置、批处理都是它的子窗口，所以 ShutdownMode 用 OnExplicitShutdown。
/// </summary>
public partial class App : Application
{
    public static ArchiveSettings Settings { get; private set; } = null!;

    public static JsonArchiveIndex Index { get; private set; } = null!;

    public static CategoryCatalog Categories { get; private set; } = null!;

    public static PanelViewModel Panel { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();

        // 安装 / 卸载入口：同一个 exe，按参数或文件名分流
        var setupOptions = SetupOptions.Parse(e.Args);
        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        var wantsUninstall = e.Args.Any(static a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var wantsInstall = e.Args.Any(static a => a.Equals("--install", StringComparison.OrdinalIgnoreCase))
            || exeName.StartsWith("DeskFlow-Setup", StringComparison.OrdinalIgnoreCase);

        if (wantsUninstall)
        {
            if (setupOptions.Silent)
            {
                var result = InstallerService.Uninstall(setupOptions.KeepUserData);
                Shutdown(result.Success ? 0 : 1);
                return;
            }

            var uninstallWindow = new UninstallWindow(setupOptions);
            MainWindow = uninstallWindow;
            uninstallWindow.Closed += (_, _) => Shutdown(0);
            uninstallWindow.Show();
            return;
        }

        if (wantsInstall)
        {
            if (setupOptions.Silent)
            {
                var result = InstallerService.Install(setupOptions);
                Shutdown(result.Success ? 0 : 1);
                return;
            }

            var installerWindow = new InstallerWindow(setupOptions);
            MainWindow = installerWindow;
            installerWindow.Closed += (_, _) => Shutdown(0);
            installerWindow.Show();
            return;
        }

        if (CliHost.IsCliInvocation(e.Args))
        {
            var exitCode = CliHost.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        try
        {
            Settings = new ArchiveSettings();
            Categories = new CategoryCatalog(
                Settings.UserCategories
                    .Where(static c => c.Length > 0)
                    .Select(static c => new CategoryDefinition(c[0], c.Skip(1))));
            Index = new JsonArchiveIndex();
            Panel = new PanelViewModel(Settings, Index, Categories);

            var panelWindow = new DesktopPanelWindow(Panel, Settings);
            MainWindow = panelWindow;
            panelWindow.Show();

            Panel.StartWatching();
            Panel.ImportRecent(TimeSpan.FromMinutes(30));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"DeskFlow 启动失败：{ex.Message}\n\n{ex.StackTrace}", "DeskFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Panel?.StopWatching();
        Settings?.Save();
        base.OnExit(e);
    }
}