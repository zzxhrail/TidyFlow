using DeskFlow.Core.Models;

namespace DeskFlow.Core.Categorization;

/// <summary>
/// 类别目录：内置类别 + 用户自定义类别。解析规则：
/// 1. 用户类别优先（同名覆盖内置）；2. 扩展名精确匹配；3. 全不中 → "其他"。
/// </summary>
public sealed class CategoryCatalog
{
    private readonly List<CategoryDefinition> _user;
    private readonly List<CategoryDefinition> _builtIn;

    public CategoryCatalog(IEnumerable<CategoryDefinition>? userCategories = null)
    {
        _user = userCategories?.ToList() ?? [];
        _builtIn = DefaultCategories.Create().ToList();
    }

    /// <summary>
    /// 用于解析与界面展示：用户类别优先；与内置同名时"扩展名取并集"——
    /// 用户给"办公文档"加了 .pages，不会把原来的 .docx/.pdf 弄丢。
    /// </summary>
    public IReadOnlyList<CategoryDefinition> All
    {
        get
        {
            var merged = new List<CategoryDefinition>(_user.Count + _builtIn.Count);
            foreach (var user in _user)
            {
                var builtIn = _builtIn.FirstOrDefault(c => string.Equals(c.Name, user.Name, StringComparison.OrdinalIgnoreCase));
                merged.Add(builtIn is null
                    ? user
                    : new CategoryDefinition(user.Name, user.Extensions.Concat(builtIn.Extensions), isBuiltIn: false, glyph: user.Glyph));
            }

            var overridden = new HashSet<string>(_user.Select(static c => c.Name), StringComparer.OrdinalIgnoreCase);
            merged.AddRange(_builtIn.Where(c => !overridden.Contains(c.Name)));
            return merged;
        }
    }

    public IReadOnlyList<CategoryDefinition> UserCategories => _user;

    public CategoryDefinition Resolve(string fileNameOrExtension)
    {
        var extension = CategoryDefinition.NormalizeExtension(Path.GetExtension(fileNameOrExtension));
        if (extension.Length == 0)
        {
            extension = CategoryDefinition.NormalizeExtension(fileNameOrExtension);
        }

        foreach (var category in All)
        {
            if (category.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return All.First(static c => c.Name == DefaultCategories.FallbackName);
    }

    /// <summary>新增或覆盖一个用户类别（同名同扩展名合并）。</summary>
    public void UpsertUserCategory(string name, IEnumerable<string> extensions)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("类别名不能为空", nameof(name));
        }

        var merged = extensions.Select(CategoryDefinition.NormalizeExtension).Where(static e => e.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = _user.FirstOrDefault(c => string.Equals(c.Name, clean, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _user.Remove(existing);
            merged = merged.Concat(existing.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        _user.Add(new CategoryDefinition(clean, merged, isBuiltIn: false, glyph: "\uE8B7"));
    }

    public bool RemoveUserCategory(string name) =>
        _user.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
}