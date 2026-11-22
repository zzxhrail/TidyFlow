namespace DeskFlow.App.Install;

/// <summary>安装/卸载命令行选项（静默模式给自动化测试与批量部署用）。</summary>
public sealed record SetupOptions(
    bool Silent,
    string? Directory,
    bool Autostart,
    bool Shortcuts,
    bool KeepUserData)
{
    public static SetupOptions Parse(string[] args)
    {
        var silent = Has(args, "--silent");
        var directory = Value(args, "--dir");
        var autostart = Flag(args, "--autostart", defaultValue: true);
        var shortcuts = Flag(args, "--shortcuts", defaultValue: true);
        var keepData = Flag(args, "--keepdata", defaultValue: true);
        return new SetupOptions(silent, directory, autostart, shortcuts, keepData);
    }

    private static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..].Trim('"');
            }

            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1].Trim('"');
            }
        }

        return null;
    }

    private static bool Flag(string[] args, string name, bool defaultValue)
    {
        var raw = Value(args, name);
        if (raw is null)
        {
            return defaultValue;
        }

        return raw is "1" or "true" or "yes" or "on";
    }
}
