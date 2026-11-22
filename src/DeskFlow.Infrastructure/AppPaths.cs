namespace DeskFlow.Infrastructure;

/// <summary>所有落盘位置集中在这里，避免程序到处写相对路径（尤其是被 UAC 拦的 Program Files）。</summary>
public static class AppPaths
{
    public const string ProductName = "DeskFlow";

    /// <summary>
    /// 数据根目录。默认 %LOCALAPPDATA%\DeskFlow；
    /// 设置环境变量 DESKFLOW_HOME 可切到任意目录（便携模式 / 自动化测试用，不污染真实配置）。
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DESKFLOW_HOME");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, ProductName);
        }
    }

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public static string DefaultOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ProductName, "output");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}