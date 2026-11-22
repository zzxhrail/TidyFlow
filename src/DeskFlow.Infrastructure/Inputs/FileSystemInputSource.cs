using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Infrastructure.Inputs;

/// <summary>目录输入源：递归枚举文件，跳过系统属性与隐藏目录，结果按相对路径排序保证可复现。</summary>
public sealed class FileSystemInputSource : IInputSource
{
    private readonly int _maxFiles;

    public FileSystemInputSource(int maxFiles = 200_000) => _maxFiles = maxFiles;

    public string Name => "folder";

    public string Description => "扫描一个目录（可递归），把每个文件作为一条输入";

    public Task<IReadOnlyList<InputItem>> CollectAsync(JobContext context, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(context.InputRoot);
        if (File.Exists(root))
        {
            InputItem single = ToItem(root, Path.GetDirectoryName(root) ?? root);
            return Task.FromResult<IReadOnlyList<InputItem>>([single]);
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"输入路径不存在: {root}");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = context.Recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        var list = new List<InputItem>();
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (list.Count >= _maxFiles)
            {
                context.LogWarn($"文件数超过上限 {_maxFiles}，其余已跳过");
                break;
            }

            list.Add(ToItem(file, root));
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return Task.FromResult<IReadOnlyList<InputItem>>(list);
    }

    private static InputItem ToItem(string file, string root)
    {
        var info = new FileInfo(file);
        var relative = Path.GetRelativePath(root, file);
        return new InputItem(
            info.FullName,
            relative,
            info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.UtcNow);
    }
}
