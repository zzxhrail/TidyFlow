namespace DeskFlow.Core.Models;

/// <summary>
/// 一个归档类别。内置类别由代码给出，用户自定义类别同名时以用户定义为准（可覆盖扩展名归属）。
/// </summary>
public sealed class CategoryDefinition
{
    public CategoryDefinition(string name, IEnumerable<string> extensions, bool isBuiltIn = false, string glyph = "")
    {
        Name = name;
        Extensions = extensions
            .Select(NormalizeExtension)
            .Where(static e => e.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IsBuiltIn = isBuiltIn;
        Glyph = glyph;
    }

    public string Name { get; }

    public IReadOnlyList<string> Extensions { get; }

    public bool IsBuiltIn { get; }

    /// <summary>Segoe MDL2 Assets 字形，面板上用。</summary>
    public string Glyph { get; }

    public static string NormalizeExtension(string extension)
    {
        var value = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value[0] == '.' ? value : "." + value;
    }
}
