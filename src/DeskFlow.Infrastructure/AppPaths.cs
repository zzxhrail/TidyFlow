namespace DeskFlow.Infrastructure;

/// <summary>所有落盘位置集中在这里，避免程序到处写相对路径（尤其是被 UAC 拦的 Program Files）。</summary>
public static class AppPaths
{
    public const string ProductName = "DeskFlow";

    public static string RootDirectory
    {
        get
        {
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
