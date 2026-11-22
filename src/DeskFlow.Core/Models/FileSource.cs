namespace DeskFlow.Core.Models;

/// <summary>文件从哪个聊天工具/位置来的。用于归档记录展示与"按来源自动下载"开关。</summary>
public enum FileSource
{
    Unknown = 0,
    Qq = 1,
    WeChat = 2,
    DingTalk = 3,
    Downloads = 4,
    Manual = 5,
}

public static class FileSourceExtensions
{
    public static string DisplayName(this FileSource source) => source switch
    {
        FileSource.Qq => "QQ",
        FileSource.WeChat => "微信",
        FileSource.DingTalk => "钉钉",
        FileSource.Downloads => "下载目录",
        FileSource.Manual => "手动添加",
        _ => "未知",
    };

    public static string Key(this FileSource source) => source switch
    {
        FileSource.Qq => "qq",
        FileSource.WeChat => "wechat",
        FileSource.DingTalk => "dingtalk",
        FileSource.Downloads => "downloads",
        FileSource.Manual => "manual",
        _ => "unknown",
    };
}
