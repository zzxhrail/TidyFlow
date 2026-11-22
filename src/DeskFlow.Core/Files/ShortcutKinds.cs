using System.IO;

namespace DeskFlow.Core.Files;

/// <summary>
/// 「快捷方式」类文件（.lnk / .url）：拖进面板时只登记成快捷方式，不参与归档分类。
/// </summary>
public static class ShortcutKinds
{
    public static bool IsShortcut(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }
}
