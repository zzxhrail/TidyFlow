using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Watch;

/// <summary>
/// 探测 QQ / 微信 / 钉钉 / 下载目录的收文件路径。
/// 聊天工具不提供稳定的文件回调，业界做法就是盯它们的落盘目录 —— 换版本只改这里的候选路径。
/// </summary>
public static class SourceDirectoryResolver
{
    public static IReadOnlyList<WatchTarget> Discover(string? userProfile = null)
    {
        var profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Path.Combine(profile, "Documents");
        var targets = new List<WatchTarget>();

        // QQ：Documents\Tencent Files\<QQ号>\FileRecv
        foreach (var account in SafeDirectories(documents, "Tencent Files"))
        {
            Add(targets, "QQ 接收文件", Path.Combine(account, "FileRecv"), FileSource.Qq);
        }

        // 微信 3.x：Documents\WeChat Files\<wxid>\FileStorage\File
        foreach (var account in SafeDirectories(documents, "WeChat Files"))
        {
            Add(targets, "微信接收文件", Path.Combine(account, "FileStorage", "File"), FileSource.WeChat);
        }

        // 微信 4.x：Documents\xwechat_files\<wxid>\msg\file
        foreach (var account in SafeDirectories(documents, "xwechat_files"))
        {
            Add(targets, "微信接收文件(4.x)", Path.Combine(account, "msg", "file"), FileSource.WeChat);
            Add(targets, "微信接收文件(4.x)", Path.Combine(account, "msg", "attach"), FileSource.WeChat);
        }

        // 钉钉：Documents\DingTalk\<user>\file 与 %USERPROFILE%\DingTalk\files
        foreach (var account in SafeDirectories(documents, "DingTalk"))
        {
            Add(targets, "钉钉接收文件", Path.Combine(account, "file"), FileSource.DingTalk);
            Add(targets, "钉钉接收文件", account, FileSource.DingTalk);
        }

        Add(targets, "钉钉接收文件", Path.Combine(profile, "DingTalk", "files"), FileSource.DingTalk);

        // 浏览器/手动下载
        Add(targets, "下载目录", Path.Combine(profile, "Downloads"), FileSource.Downloads);

        return targets
            .GroupBy(static t => t.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .ToList();
    }

    private static void Add(List<WatchTarget> targets, string name, string directory, FileSource source)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        targets.Add(new WatchTarget(name, directory, source, IsDetected: true));
    }

    private static IEnumerable<string> SafeDirectories(string parent, string child)
    {
        var root = Path.Combine(parent, child);
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
