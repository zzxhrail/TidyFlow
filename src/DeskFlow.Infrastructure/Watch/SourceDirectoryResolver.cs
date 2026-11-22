using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Watch;

/// <summary>
/// 探测 QQ / 微信 / 钉钉 / 下载目录的收文件位置。
///
/// 关键点（踩过的坑）：
/// 1. **不能用 %USERPROFILE%\Documents** —— 用户把"文档"重定向到 D:\docu 之后那里什么都没有。
///    必须用 Environment.SpecialFolder.MyDocuments，它才跟着重定向走。
/// 2. QQNT 9.x 的落盘位置是 账号目录\nt_qq\nt_data\File（收文件在 File\FileRecv），
///    旧版才是 账号目录\FileRecv；两套都探测。
/// 3. 微信 4.x 是 wxid 目录\msg\file\YYYY-MM；3.x 是 wxid 目录\FileStorage\File。
/// 4. 目录可能"还没被创建"（客户端没收到过文件），这时按预测路径给出 target，
///    监听器会在目录出现后自动生效，界面上显示"等待首次接收"。
/// </summary>
public static class SourceDirectoryResolver
{
    public static IReadOnlyList<WatchTarget> Discover(
        string? userProfile = null,
        string? documentsRoot = null,
        IEnumerable<string>? cachedRoots = null,
        string? appDataRoot = null)
    {
        var appData = appDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = documentsRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var targets = new List<WatchTarget>();

        // 文档目录可能是空串（极端环境），退回 profile\Documents
        if (string.IsNullOrWhiteSpace(documents))
        {
            documents = Path.Combine(profile, "Documents");
        }

        var dataRoots = new List<string> { documents };
        var fallbackDocuments = Path.Combine(profile, "Documents");
        if (!string.Equals(fallbackDocuments, documents, StringComparison.OrdinalIgnoreCase))
        {
            dataRoots.Add(fallbackDocuments);
        }

        if (cachedRoots is not null)
        {
            dataRoots.AddRange(cachedRoots.Where(static r => !string.IsNullOrWhiteSpace(r)));
        }

        foreach (var root in dataRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddQq(targets, root);
            AddWeChat(targets, root);
            AddDingTalk(targets, root);
        }

        AddDingTalk(targets, Path.Combine(profile, "DingTalk"));
        AddDingTalkPrediction(targets, documents, appData);
        AddDownloads(targets, profile);

        return targets
            .GroupBy(static t => t.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.First())
            .ToList();
    }

    private static void AddQq(List<WatchTarget> targets, string root)
    {
        var tencentFiles = Path.Combine(root, "Tencent Files");
        if (!Directory.Exists(tencentFiles))
        {
            return;
        }

        foreach (var account in SafeDirectories(tencentFiles))
        {
            // 有些机器会多一层 Tencent Files\Tencent Files\<账号>
            var accountName = Path.GetFileName(account);
            if (string.Equals(accountName, "Tencent Files", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var inner in SafeDirectories(account))
                {
                    AddQqAccount(targets, inner);
                }

                continue;
            }

            AddQqAccount(targets, account);
        }
    }

    private static void AddQqAccount(List<WatchTarget> targets, string account)
    {
        // QQNT 9.x：监听整个 nt_qq\nt_data —— 收到的文件可能落在 File\Ori、
        // File\FileRecv、dataline 等不同子目录，只盯 File 会漏；缓存目录由监听器过滤。
        var ntData = Path.Combine(account, "nt_qq", "nt_data");
        var ntFile = Path.Combine(ntData, "File");
        if (Directory.Exists(ntData))
        {
            targets.Add(new WatchTarget($"QQ 接收文件（{Path.GetFileName(account)}）", ntData, FileSource.Qq, IsDetected: true));
            return;
        }

        if (Directory.Exists(ntFile))
        {
            targets.Add(new WatchTarget($"QQ 接收文件（{Path.GetFileName(account)}）", ntFile, FileSource.Qq, IsDetected: true));
            return;
        }

        // 旧版 QQ：<账号>\FileRecv
        var legacy = Path.Combine(account, "FileRecv");
        if (Directory.Exists(legacy))
        {
            targets.Add(new WatchTarget($"QQ 接收文件（{Path.GetFileName(account)}）", legacy, FileSource.Qq, IsDetected: true));
            return;
        }

        // 账号目录在、但还没收过文件：给 QQNT 的预测路径，目录出现即生效
        targets.Add(new WatchTarget($"QQ 接收文件（{Path.GetFileName(account)}）", ntData, FileSource.Qq, IsDetected: false));
    }

    private static void AddWeChat(List<WatchTarget> targets, string root)
    {
        // 微信 4.x：xwechat_files\<wxid>\msg\file
        foreach (var account in SafeDirectories(Path.Combine(root, "xwechat_files")))
        {
            var file = Path.Combine(account, "msg", "file");
            targets.Add(new WatchTarget(
                $"微信接收文件（{ShortName(account)}）",
                file,
                FileSource.WeChat,
                IsDetected: Directory.Exists(file)));
        }

        // 微信 3.x：WeChat Files\<wxid>\FileStorage\File
        foreach (var account in SafeDirectories(Path.Combine(root, "WeChat Files")))
        {
            var file = Path.Combine(account, "FileStorage", "File");
            targets.Add(new WatchTarget(
                $"微信接收文件（{ShortName(account)}）",
                file,
                FileSource.WeChat,
                IsDetected: Directory.Exists(file)));
        }
    }

    private static void AddDingTalk(List<WatchTarget> targets, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // 传进来的既可能是文档根（需要再进一层 DingTalk），也可能已经是 DingTalk 根
        var dingRoot = string.Equals(Path.GetFileName(root), "DingTalk", StringComparison.OrdinalIgnoreCase)
            ? root
            : Path.Combine(root, "DingTalk");

        if (!Directory.Exists(dingRoot))
        {
            return;
        }

        // 钉钉：DingTalk\<用户名>\file
        var accounts = SafeDirectories(dingRoot).ToList();
        foreach (var account in accounts)
        {
            var file = Path.Combine(account, "file");
            targets.Add(new WatchTarget(
                $"钉钉接收文件（{ShortName(account)}）",
                file,
                FileSource.DingTalk,
                IsDetected: Directory.Exists(file)));
        }

        // 没有账号子目录时：老版本可能直接把文件放在 DingTalk 根下（预测路径，等待创建）
        if (accounts.Count == 0)
        {
            targets.Add(new WatchTarget("钉钉接收文件", dingRoot, FileSource.DingTalk, IsDetected: false));
        }
    }

    /// <summary>
    /// 钉钉装了但还没收过文件时，目录不会存在。此时按它的默认位置给出预测目标，
    /// 监听器每轮都会重试，目录一被创建（第一次收文件）就开始工作。
    /// 判定"装了"用 %APPDATA%\DingTalk 是否存在，避免误判。
    /// </summary>
    private static void AddDingTalkPrediction(List<WatchTarget> targets, string documents, string appData)
    {
        var installed = Directory.Exists(Path.Combine(appData, "DingTalk")) || Directory.Exists(Path.Combine(appData, "dinglive"));
        if (!installed)
        {
            return;
        }

        var dingRoot = Path.Combine(documents, "DingTalk");
        if (!Directory.Exists(dingRoot))
        {
            targets.Add(new WatchTarget("钉钉接收文件（等待首次接收）", dingRoot, FileSource.DingTalk, IsDetected: false));
        }
    }

    private static void AddDownloads(List<WatchTarget> targets, string profile)
    {
        var downloads = Path.Combine(profile, "Downloads");
        if (Directory.Exists(downloads))
        {
            targets.Add(new WatchTarget("下载目录", downloads, FileSource.Downloads, IsDetected: true));
        }

    }

    private static string ShortName(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.Length <= 12 ? name : name[..12] + "…";
    }

    private static IEnumerable<string> SafeDirectories(string parent)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.GetDirectories(parent) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}