using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DeskFlow.Deployment;

/// <summary>安装 / 卸载的结果；成功时 Message 里带目标目录（安装）或安装目录（卸载）。</summary>
public sealed record DeploymentResult(bool Success, string Message);

/// <summary>
/// 安装布局与系统集成：装到哪、快捷方式、开机自启、卸载注册项。
/// 安装程序（DeskFlow.Setup）与程序自身的卸载窗口共用这一份实现，
/// 两边逻辑一旦分叉就会出现"卸载不干净"。
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallLayout
{
    public const string ProductName = "TidyFlow";
    public const string DisplayName = "DeskFlow 文件管家";
    public const string Publisher = "DeskFlow";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductName;
    private const string RunValueName = ProductName;
    private const string StartMenuFolderName = ProductName;

    /// <summary>默认安装目录：%LOCALAPPDATA%\Programs\DeskFlow（不需要管理员）。</summary>
    public static string DefaultInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        ProductName);

    public static string ExePath(string installDirectory) => Path.Combine(installDirectory, ProductName + ".exe");

    public static string StartMenuDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", StartMenuFolderName);

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ProductName + ".lnk");

    public static string StartMenuShortcutPath => Path.Combine(StartMenuDirectory, ProductName + ".lnk");

    /// <summary>读注册表里记录的安装目录；没装过返回 null。</summary>
    public static string? GetInstalledDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        var location = key?.GetValue("InstallLocation") as string;
        if (!string.IsNullOrWhiteSpace(location) && File.Exists(ExePath(location)))
        {
            return location;
        }

        return null;
    }

    public static string? GetInstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("DisplayVersion") as string;
    }

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string value && value.Length > 0;
    }

    public static string? GetAutostartCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) as string;
    }

    /// <summary>开机自启：写 HKCU\...\Run，不碰计划任务（不需要管理员）。</summary>
    public static void SetAutostart(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{exePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>创建快捷方式（桌面 / 开始菜单）。用 WScript.Shell，避免手写 IShellLink 的 COM 声明。</summary>
    public static void CreateShortcuts(string installDirectory, bool desktop, bool startMenu)
    {
        var exe = ExePath(installDirectory);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("找不到主程序", exe);
        }

        if (desktop)
        {
            CreateShortcut(DesktopShortcutPath, exe, installDirectory, DisplayName);
        }

        if (startMenu)
        {
            Directory.CreateDirectory(StartMenuDirectory);
            CreateShortcut(StartMenuShortcutPath, exe, installDirectory, DisplayName);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统缺少 WScript.Shell，无法创建快捷方式");

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic dshell = shell!;
            dynamic link = dshell.CreateShortcut(shortcutPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workingDirectory;
            link.IconLocation = targetPath + ",0";
            link.Description = description;
            link.WindowStyle = 1;
            link.Save();
        }
        finally
        {
            if (shell is not null)
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
    }

    public static void RemoveShortcuts()
    {
        SafeDelete(DesktopShortcutPath);
        SafeDelete(StartMenuShortcutPath);
        try
        {
            if (Directory.Exists(StartMenuDirectory) && Directory.GetFileSystemEntries(StartMenuDirectory).Length == 0)
            {
                Directory.Delete(StartMenuDirectory);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>写"设置 → 应用"里的卸载项；卸载命令指向程序自己的 --uninstall。</summary>
    public static void WriteUninstallEntry(string installDirectory, string displayVersion, long estimatedSizeKb)
    {
        var exe = ExePath(installDirectory);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true);
        key.SetValue("DisplayName", DisplayName + "（仅当前用户）", RegistryValueKind.String);
        key.SetValue("DisplayVersion", displayVersion, RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("DisplayIcon", exe + ",0", RegistryValueKind.String);
        key.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
        key.SetValue("UninstallString", "\"" + exe + "\" --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --silent", RegistryValueKind.String);
        key.SetValue("EstimatedSize", (int)Math.Max(1, estimatedSizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
    }

    public static void RemoveUninstallEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 删除安装目录：正在运行的 exe 删不掉自己，
    /// 所以交给一个临时 cmd 等进程退出后再 rmdir，并顺手删掉自己。
    /// </summary>
    /// <summary>
    /// 删除安装目录：正在运行的 exe 删不掉自己，交给一个隐藏的 PowerShell 子进程：
    /// 等几秒 → 反复重试删除（文件句柄释放有延迟）。
    /// 不用 .cmd 脚本 —— 脚本文件是 UTF-8、cmd 按 GBK 读，中文路径会被读坏。
    /// </summary>
    /// <summary>
    /// 删除安装目录：正在运行的 exe 删不掉自己，交给一个隐藏的 PowerShell 脚本：
    /// 等几秒 → 反复重试删除（句柄释放有延迟）。
    ///
    /// 为什么用 .ps1 文件而不是 -Command 或 .cmd：
    /// - .cmd 由 cmd.exe 按本地代码页解析，路径里的中文会被读坏；
    /// - -Command 要经过命令行引号转义，路径含空格/分号时容易炸；
    /// - .ps1 用 UTF-8 BOM 保存，PowerShell 一定按 Unicode 读，最稳。
    /// </summary>
    public static void ScheduleDirectoryRemoval(string installDirectory, int delaySeconds = 2)
    {
        var script = Path.Combine(Path.GetTempPath(), "deskflow-uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
        var literal = installDirectory.Replace("'", "''", StringComparison.Ordinal);
        var content = string.Join(Environment.NewLine,
        [
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$dir = '" + literal + "'",
            "Start-Sleep -Seconds " + Math.Max(1, delaySeconds),
            "for ($i = 0; $i -lt 40; $i++) {",
            "    if (-not (Test-Path -LiteralPath $dir)) { break }",
            "    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue",
            "    Start-Sleep -Milliseconds 500",
            "}",
            "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
            string.Empty,
        ]);

        // UTF-8 带 BOM，PowerShell 5.1 才认得出是 Unicode
        File.WriteAllText(script, content, new System.Text.UTF8Encoding(true));

        try
        {
            var process = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"" + script + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            Log($"schedule removal dir={installDirectory} pid={process?.Id} script={script}");
        }
        catch (Exception ex)
        {
            Log($"schedule removal FAILED {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "deskflow-deploy.log"),
                DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine,
                new System.Text.UTF8Encoding(true));
        }
        catch (IOException)
        {
        }
    }

    public static long DirectorySizeKb(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(static f => new FileInfo(f).Length) / 1024;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}