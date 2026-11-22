using System.Windows;
using DeskFlow.App.Cli;
using DeskFlow.Infrastructure;

namespace DeskFlow.App;

/// <summary>
/// 同一个可执行文件同时是 GUI 和命令行工具：
///   DeskFlow.exe                       → 打开窗口
///   DeskFlow.exe --run "D:\data" --out "D:\out"  → 无界面批处理，供计划任务调用
/// 这样只需要维护一个发布物，脚本化与人工操作走同一套引擎。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();

        if (CliHost.IsCliInvocation(e.Args))
        {
            var exitCode = CliHost.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
