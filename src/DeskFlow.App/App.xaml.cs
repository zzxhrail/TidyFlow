using System.Diagnostics;
using System.IO;
using System.Windows;
using DeskFlow.App.Cli;
using DeskFlow.App.Install;
using DeskFlow.App.Services;
using DeskFlow.App.ViewModels;
using DeskFlow.Core.Categorization;
using DeskFlow.Core.Models;
using DeskFlow.Infrastructure;
using DeskFlow.Infrastructure.Index;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App;

/// <summary>
/// 组合根 + 双入口：
///   TidyFlow.exe             → 桌面面板（常驻）
///   TidyFlow.exe --run ...   → 无界面批处理（计划任务/脚本）
/// 面板是主窗口；设置、批处理都是它的子窗口，所以 ShutdownMode 用 OnExplicitShutdown。
/// </summary>
public partial class App : Application
{
    public static ArchiveSettings Settings { get; private set; } = null!;

    public static JsonArchiveIndex Index { get; private set; } = null!;

    public static CategoryCatalog Categories { get; private set; } = null!;

    public static PanelViewModel Panel { get; private set; } = null!;

    /// <summary>系统托盘（面板收起来后程序继续在这里）。</summary>
    public static TrayIconService? Tray { get; private set; }

    /// <summary>命令行试分类：只打印判定结果与依据，不动文件。</summary>
    private static bool ClassifyFromCommandLine(string target)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        var classifier = new Infrastructure.Analysis.KeywordContentClassifier();
        var catalog = new Core.Categorization.CategoryCatalog();
        var files = File.Exists(target)
            ? [target]
            : Directory.Exists(target) ? Directory.GetFiles(target, "*", SearchOption.TopDirectoryOnly) : [];

        if (files.Length == 0)
        {
            Console.WriteLine("没找到文件：" + target);
            return false;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var byContent = classifier.Classify(file, name);
            var byExtension = catalog.Resolve(name).Name;

            var line = byContent is null
                ? $"{name} → {byExtension}（扩展名规则；内容判不准）"
                : $"{name} → {byContent.Category}（内容识别 {byContent.Score} 分：{byContent.Reason}）";
            Console.WriteLine(line);
        }

        return true;
    }

    /// <summary>命令行归档：读完设置后按同一套规则（含内容分类）归档，输出结果。</summary>
    private static int ImportFromCommandLine(string target)
    {
        try
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
            }
            catch (IOException)
            {
            }

            var settings = new Infrastructure.Settings.ArchiveSettings();
            var catalog = new Core.Categorization.CategoryCatalog();
            var archiver = new Infrastructure.Archive.FileArchiver(catalog, new Infrastructure.Analysis.KeywordContentClassifier());
            var files = File.Exists(target)
                ? [target]
                : Directory.Exists(target) ? Directory.GetFiles(target, "*", SearchOption.TopDirectoryOnly) : [];

            if (files.Length == 0)
            {
                Console.WriteLine("没找到可归档的文件：" + target);
                return 2;
            }

            var ok = 0;
            foreach (var file in files)
            {
                var record = new Core.Models.FileRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourcePath = file,
                    FileName = Path.GetFileName(file),
                    SizeBytes = new FileInfo(file).Length,
                    Source = Core.Models.FileSource.Manual,
                    Category = "其他",
                    DetectedAt = DateTimeOffset.Now,
                };

                var outcome = archiver.ArchiveAsync(record, new Core.Models.ArchiveOptions
                {
                    ArchiveRoot = settings.ArchiveRoot,
                    MoveInsteadOfCopy = settings.MoveInsteadOfCopy,
                    UseContentClassification = settings.ContentClassification,
                    UnrecognizedBucket = settings.UnrecognizedBucket,
                }).GetAwaiter().GetResult();

                if (outcome.Success)
                {
                    ok++;
                    Console.WriteLine($"{record.FileName} → {record.Category}（{record.CategoryReason}）");
                }
                else
                {
                    Console.WriteLine($"{record.FileName} 失败：{outcome.Error}");
                }
            }

            Console.WriteLine($"完成 {ok}/{files.Length}");
            return ok == files.Length ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("归档失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>卡退类问题必须留证据：任何未处理异常都写一份 crash.log。</summary>
    private static void HookCrashLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrash("AppDomain", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrash("UnobservedTask", args.Exception);
            args.SetObserved();
        };

        Current.DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash("Dispatcher", args.Exception);
        };
    }

    private static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(Infrastructure.AppPaths.RootDirectory, "crash.log");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(path, text);
        }
        catch (IOException)
        {
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        HookCrashLogging();
        base.OnStartup(e);
        AppPaths.EnsureCreated();

        // 安装 / 卸载入口：同一个 exe，按参数或文件名分流
        var setupOptions = SetupOptions.Parse(e.Args);
        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        var wantsUninstall = e.Args.Any(static a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        var wantsInstall = e.Args.Any(static a => a.Equals("--install", StringComparison.OrdinalIgnoreCase))
            || exeName.StartsWith("TidyFlow-Setup", StringComparison.OrdinalIgnoreCase)
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

            // --classify <文件或目录>：只看内容分类结果，不归档（调参/验证用）
            var classifyIndex = Array.FindIndex(e.Args, static x => x.Equals("--classify", StringComparison.OrdinalIgnoreCase));
            if (classifyIndex >= 0 && classifyIndex + 1 < e.Args.Length)
            {
                Shutdown(ClassifyFromCommandLine(e.Args[classifyIndex + 1]) ? 0 : 1);
                return;
            }

            // --scan：直接打开扫描磁盘窗口
            if (e.Args.Any(static x => x.Equals("--scan", StringComparison.OrdinalIgnoreCase)))
            {
                var scanWindow = new Scan.ScanWindow(Panel);
                MainWindow = scanWindow;
                scanWindow.Closed += (_, _) => Shutdown(0);
                scanWindow.Show();
                return;
            }

            // --import <文件或目录>：按当前设置归档一次（内容分类也生效），给脚本/右键菜单用
            var importIndex = Array.FindIndex(e.Args, static x => x.Equals("--import", StringComparison.OrdinalIgnoreCase));
            if (importIndex >= 0 && importIndex + 1 < e.Args.Length)
            {
                var exitCode = ImportFromCommandLine(e.Args[importIndex + 1]);
                Shutdown(exitCode);
                return;
            }

            // --preview <文件>：直接开预览窗口（也方便做"用 TidyFlow 预览"的右键菜单项）
            var previewIndex = Array.FindIndex(e.Args, static x => x.Equals("--preview", StringComparison.OrdinalIgnoreCase));
            if (previewIndex >= 0 && previewIndex + 1 < e.Args.Length)
            {
                var target = e.Args[previewIndex + 1];
                var tile = new ViewModels.PanelTile
                {
                    Kind = ViewModels.TileKind.Pending,
                    Title = Path.GetFileName(target),
                    Path = target,
                    RecordId = string.Empty,
                    Icon = Services.ShellIconProvider.ForFile(target),
                };

                var preview = new Preview.PreviewWindow(Panel, tile);
                MainWindow = preview;
                preview.Closed += (_, _) => Shutdown(0);
                preview.Show();
                return;
            }

            // --history：直接打开归档记录（可撤销）
            if (e.Args.Any(static x => x.Equals("--history", StringComparison.OrdinalIgnoreCase)))
            {
                var historyOnly = new History.ArchiveHistoryWindow(Panel);
                MainWindow = historyOnly;
                historyOnly.Closed += (_, _) => Shutdown(0);
                historyOnly.Show();
                return;
            }

            // --settings：直接打开设置（调试/给用户建快捷方式用）
            if (e.Args.Any(static x => x.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
            {
                var settingsOnly = new SettingsWindow(Panel, Settings);
                MainWindow = settingsOnly;
                settingsOnly.Closed += (_, _) => Shutdown(0);
                settingsOnly.Show();
                return;
            }

            var panelWindow = new DesktopPanelWindow(Panel, Settings);
            MainWindow = panelWindow;
            panelWindow.Show();

            Tray = new TrayIconService(
                Panel,
                Settings,
                showPanel: ShowPanelFromTray,
                openSettings: OpenSettingsFromTray,
                openHistory: OpenHistoryFromTray,
                restart: RestartApplication,
                exit: ExitApplication);

            Panel.StartWatching();
            Panel.ImportRecent(TimeSpan.FromMinutes(30));
            Panel.StartGlobalWatch();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"TidyFlow 启动失败：{ex.Message}\n\n{ex.StackTrace}", "TidyFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    /// <summary>
    /// 托盘"打开面板"：面板被意外关掉（例如某些情况下 Alt+F4）时重新建一个，
    /// 而不是让托盘图标点不动。
    /// </summary>
    private static void ShowPanelFromTray()
    {
        if (Current.MainWindow is DesktopPanelWindow existing && existing.IsLoaded)
        {
            existing.ShowFromTray();
            return;
        }

        var panel = new DesktopPanelWindow(Panel, Settings);
        Current.MainWindow = panel;
        panel.Show();
    }

    /// <summary>托盘菜单"归档记录"：打开可撤销列表。</summary>
    private static void OpenHistoryFromTray()
    {
        var owner = Current.MainWindow is { IsVisible: true } ? Current.MainWindow : null;
        var window = new History.ArchiveHistoryWindow(Panel);

        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        Panel.RefreshAll();
    }

    /// <summary>托盘菜单"设置…"：面板开着就以它为 owner，否则独立弹。</summary>
    private static void OpenSettingsFromTray()
    {
        var owner = Current.MainWindow is { IsVisible: true } ? Current.MainWindow : null;
        var window = new SettingsWindow(Panel, Settings);

        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        Panel.RefreshAll();
    }

    /// <summary>托盘菜单"重新启动"：起一个新进程再退出自己，配置已先落盘。</summary>
    private static void RestartApplication()
    {
        Settings.Save();
        Panel.StopGlobalWatch();
        Panel.StopWatching();
        AllowPanelClose();

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
                });
            }
            catch (Exception)
            {
                // 起不来就不要退出，避免用户点了"重启"结果程序消失。
                return;
            }
        }

        Current.Shutdown(0);
    }

    private static void ExitApplication()
    {
        Settings.Save();
        Panel.StopGlobalWatch();
        Panel.StopWatching();
        AllowPanelClose();
        Current.Shutdown(0);
    }

    /// <summary>真正退出前放开窗口的"关闭即收起"拦截。</summary>
    private static void AllowPanelClose()
    {
        if (Current.MainWindow is DesktopPanelWindow panel)
        {
            panel.AllowClose = true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Panel?.StopGlobalWatch();
        Panel?.StopWatching();
        Tray?.Dispose();
        Settings?.Save();
        base.OnExit(e);
    }
}