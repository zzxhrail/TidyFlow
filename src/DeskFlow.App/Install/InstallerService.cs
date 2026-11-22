using System.IO;
using DeskFlow.Deployment;

namespace DeskFlow.App.Install;

/// <summary>
/// 安装/卸载的真正动作，界面与静默模式共用。
/// 安装 = 复制自身到目标目录 + 快捷方式 + 开机自启 + 写卸载项；
/// 卸载 = 反向清理，目录交给临时 cmd 延迟删除（正在运行的 exe 删不掉自己）。
/// </summary>
public static class InstallerService
{
    public static DeploymentResult Install(SetupOptions options, Action<string, int>? report = null)
    {
        try
        {
            var source = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                return new DeploymentResult(false, "找不到安装源文件");
            }

            var target = string.IsNullOrWhiteSpace(options.Directory)
                ? InstallLayout.DefaultInstallDirectory
                : Path.GetFullPath(options.Directory);

            report?.Invoke("准备安装目录：" + target, 5);
            Directory.CreateDirectory(target);

            var targetExe = InstallLayout.ExePath(target);
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
            {
                // 已经在目标位置运行（例如从安装目录再次点 Setup）：也要补齐快捷方式与注册项
                report?.Invoke("程序已在目标目录，仅更新集成项", 40);
            }
            else
            {
                report?.Invoke("复制程序文件（约 60 MB）…", 20);
                File.Copy(source, targetExe, overwrite: true);
                report?.Invoke("程序文件已就位", 65);
            }

            report?.Invoke("创建快捷方式…", 75);
            InstallLayout.CreateShortcuts(target, options.Shortcuts, options.Shortcuts);

            report?.Invoke(options.Autostart ? "设置开机自启动…" : "跳过开机自启", 85);
            InstallLayout.SetAutostart(options.Autostart, targetExe);

            report?.Invoke("写入卸载项…", 92);
            var version = typeof(InstallerService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            InstallLayout.WriteUninstallEntry(target, version, InstallLayout.DirectorySizeKb(target));

            report?.Invoke("安装完成", 100);
            return new DeploymentResult(true, target);
        }
        catch (Exception ex)
        {
            LogFailure("安装", ex);
            return new DeploymentResult(false, Describe(ex));
        }
    }

    /// <summary>把失败写进 install.log：真机上"点了就闪退"必须有证据可查。</summary>
    private static void LogFailure(string stage, Exception ex)
    {
        try
        {
            var path = Path.Combine(Infrastructure.AppPaths.RootDirectory, "install.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage} 失败{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "没有写入权限：" + ex.Message,
        IOException => "文件被占用或写入失败（关掉正在运行的 TidyFlow 再试）：" + ex.Message,
        _ => ex.GetType().Name + "：" + ex.Message,
    };

    public static DeploymentResult Uninstall(bool keepUserData, Action<string, int>? report = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            var installDirectory = exe is null ? null : Path.GetDirectoryName(exe);
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return new DeploymentResult(false, "找不到安装目录");
            }

            report?.Invoke("删除快捷方式…", 15);
            InstallLayout.RemoveShortcuts();

            report?.Invoke("清除开机自启…", 30);
            InstallLayout.SetAutostart(false, InstallLayout.ExePath(installDirectory));

            report?.Invoke("移除卸载项…", 45);
            InstallLayout.RemoveUninstallEntry();

            if (!keepUserData)
            {
                report?.Invoke("删除配置、日志与索引…", 60);
                TryDeleteDirectory(DeskFlow.Infrastructure.AppPaths.RootDirectory);
            }

            report?.Invoke("清理程序目录（退出后删除）…", 80);
            InstallLayout.ScheduleDirectoryRemoval(installDirectory);

            report?.Invoke("卸载完成", 100);
            return new DeploymentResult(true, installDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeploymentResult(false, ex.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
