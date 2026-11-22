using System.IO;
using DeskFlow.Infrastructure.Settings;

namespace DeskFlow.App.Services;

/// <summary>面板第一次打开时的默认快捷入口：本机的 QQ / 微信 / 钉钉 + 归档根目录。</summary>
public static class ShortcutSeeder
{
    public static IEnumerable<ShortcutItem> Seed()
    {
        var candidates = new (string Name, string[] Paths)[]
        {
            ("QQ", [@"C:\Program Files\Tencent\QQNT\QQ.exe", @"C:\Program Files (x86)\Tencent\QQ\Bin\QQ.exe"]),
            ("微信", [@"C:\Program Files\Tencent\WeChat\WeChat.exe", @"C:\Program Files (x86)\Tencent\WeChat\WeChat.exe"]),
            ("钉钉", [@"C:\Program Files (x86)\DingDing\DingtalkLauncher.exe", @"C:\Program Files\DingTalk\DingtalkLauncher.exe"]),
        };

        foreach (var (name, paths) in candidates)
        {
            var found = paths.FirstOrDefault(File.Exists);
            if (found is not null)
            {
                yield return new ShortcutItem { Name = name, Path = found, IsFolder = false };
            }
        }

        var archive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DeskFlow", "output", "archive");
        yield return new ShortcutItem { Name = "归档目录", Path = archive, IsFolder = true };
    }
}