using System.IO;

namespace DeskFlow.Infrastructure.Settings;

/// <summary>
/// 把「快捷方式类文件」（.lnk / .url）登记进面板快捷方式列表。
/// 抽到 Infrastructure 是为了能被自检覆盖：拖放分支只做分流，真正的登记逻辑在这里。
/// </summary>
public static class ShortcutRegistry
{
    public static bool IsShortcutFile(string path) =>
        DeskFlow.Core.Files.ShortcutKinds.IsShortcut(path);

    /// <summary>
    /// 登记一批路径为面板快捷方式（不做复制、不分类、不进归档目录）。
    /// 重复路径会跳过；返回真正新增的数量。
    /// </summary>
    public static int Add(ArchiveSettings settings, IEnumerable<string> paths, bool save = true)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (settings.Shortcuts.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            settings.Shortcuts.Add(new ShortcutItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Path = path,
                IsFolder = false,
            });

            added++;
        }

        if (added > 0 && save)
        {
            settings.Save();
        }

        return added;
    }
}
