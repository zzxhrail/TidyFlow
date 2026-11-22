using DeskFlow.Core.Models;

namespace DeskFlow.Core.Categorization;

/// <summary>内置类别表。加一类 = 这里加一行，用户不用改代码也能在设置里自定义。</summary>
public static class DefaultCategories
{
    public const string FallbackName = "其他";

    public static IReadOnlyList<CategoryDefinition> Create() =>
    [
        new("办公文档",
            [".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf", ".csv", ".one", ".vsdx"],
            isBuiltIn: true, glyph: "\uE8A5"),
        new("代码",
            [".cs", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".go", ".rs", ".c", ".h", ".cpp", ".hpp", ".json", ".xml",
             ".yml", ".yaml", ".sql", ".ps1", ".bat", ".cmd", ".sh", ".html", ".css", ".vue", ".sln", ".csproj", ".ipynb"],
            isBuiltIn: true, glyph: "\uE943"),
        new("图片",
            [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".psd", ".ai", ".tif", ".tiff", ".heic"],
            isBuiltIn: true, glyph: "\uEB9F"),
        new("音视频",
            [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg"],
            isBuiltIn: true, glyph: "\uE8D6"),
        new("压缩包",
            [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso"],
            isBuiltIn: true, glyph: "\uE7B8"),
        new("安装包",
            [".exe", ".msi", ".apk", ".dmg", ".pkg", ".deb", ".rpm"],
            isBuiltIn: true, glyph: "\uE896"),
        new(FallbackName, [], isBuiltIn: true, glyph: "\uE8A5"),
    ];
}
