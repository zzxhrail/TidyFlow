using System.Diagnostics;

namespace DeskFlow.Infrastructure.Services;

/// <summary>用系统默认程序打开文件/文件夹，或在资源管理器中定位。失败只返回 false，不抛异常到 UI。</summary>
public static class ShellLauncher
{
    public static bool Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }

            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }
}
