using System.IO;
using DeskFlow.Infrastructure;

namespace DeskFlow.App.Services;

/// <summary>
/// 轻量诊断：设置 DESKFLOW_DEBUG=1 时把面板密度计算写进 %LOCALAPPDATA%\DeskFlow\panel-layout.log。
/// 面板"缩放后排版不对"这类问题必须看真实数值，猜没用。
/// </summary>
public static class Diagnostics
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("DESKFLOW_DEBUG") == "1";
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.RootDirectory);
                File.AppendAllText(
                    Path.Combine(AppPaths.RootDirectory, "panel-layout.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }
}
