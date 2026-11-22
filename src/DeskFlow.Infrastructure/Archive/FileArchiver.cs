using System.Security.Cryptography;
using DeskFlow.Core.Categorization;
using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Archive;

/// <summary>
/// 归档器：按类别把文件放进 &lt;归档根&gt;/&lt;类别&gt;/，同名同内容跳过，同名不同内容自动加 (2)(3)。
/// 只做文件移动/复制，不删除原件（除非显式选择"移动"）。
/// </summary>
public sealed class FileArchiver
{
    private const int HashSampleBytes = 1024 * 1024;

    private readonly CategoryCatalog _categories;
    private readonly DeskFlow.Core.Analysis.IContentClassifier? _classifier;

    public FileArchiver(CategoryCatalog categories, DeskFlow.Core.Analysis.IContentClassifier? classifier = null)
    {
        _categories = categories;
        _classifier = classifier;
    }

    public async Task<ArchiveOutcome> ArchiveAsync(FileRecord record, ArchiveOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(record.SourcePath))
            {
                record.Status = ArchiveStatus.Failed;
                record.Error = "源文件已不存在";
                return ArchiveOutcome.Failed(record, record.Error);
            }

            // 优先顺序：拖到某分类（强制） > 内容识别 > 扩展名规则
            string categoryName;
            if (!string.IsNullOrWhiteSpace(options.CategoryOverride))
            {
                categoryName = options.CategoryOverride!;
                record.CategoryReason = "手动指定";
            }
            else
            {
                var byContent = options.UseContentClassification ? _classifier?.Classify(record.SourcePath, record.FileName) : null;
                if (byContent is not null)
                {
                    categoryName = byContent.Category;
                    record.CategoryReason = $"内容识别（{byContent.Reason}）";
                }
                else
                {
                    categoryName = _categories.Resolve(record.FileName).Name;
                    record.CategoryReason ??= "扩展名规则";
                }
            }

            record.Category = categoryName;

            var categoryDirectory = Path.Combine(options.ArchiveRoot, SanitizeFolderName(categoryName));
            Directory.CreateDirectory(categoryDirectory);

            var targetPath = Path.Combine(categoryDirectory, record.FileName);
            if (File.Exists(targetPath))
            {
                var existingHash = await SampleHashAsync(targetPath, cancellationToken).ConfigureAwait(false);
                var incomingHash = await SampleHashAsync(record.SourcePath, cancellationToken).ConfigureAwait(false);
                record.Hash = incomingHash;

                if (options.SkipDuplicates && string.Equals(existingHash, incomingHash, StringComparison.Ordinal))
                {
                    record.ArchivedPath = targetPath;
                    record.Status = ArchiveStatus.Archived;
                    record.ArchivedAt = DateTimeOffset.Now;
                    record.Error = null;
                    return ArchiveOutcome.Duplicated(record);
                }

                targetPath = NextFreePath(categoryDirectory, record.FileName);
            }

            if (options.MoveInsteadOfCopy)
            {
                File.Move(record.SourcePath, targetPath);
            }
            else
            {
                await CopyAsync(record.SourcePath, targetPath, cancellationToken).ConfigureAwait(false);
            }

            record.ArchivedPath = targetPath;
            record.Status = ArchiveStatus.Archived;
            record.ArchivedAt = DateTimeOffset.Now;
            record.Error = null;
            return ArchiveOutcome.Ok(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            record.Status = ArchiveStatus.Failed;
            record.Error = ex.Message;
            return ArchiveOutcome.Failed(record, ex.Message);
        }
    }

    /// <summary>
    /// 撤销归档：把文件从归档目录搬回原位置。
    /// 原位置已被别的文件占用时，退回"名字 (2).ext"，并记在 RestoredPath 上，不覆盖任何东西。
    /// </summary>
    public async Task<ArchiveOutcome> UndoAsync(FileRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(record.ArchivedPath) || !File.Exists(record.ArchivedPath))
            {
                record.Status = ArchiveStatus.Failed;
                record.Error = "归档文件已不在原处，无法撤销";
                return ArchiveOutcome.Failed(record, record.Error);
            }

            var target = record.SourcePath;
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(directory))
            {
                record.Status = ArchiveStatus.Failed;
                record.Error = "原位置无效";
                return ArchiveOutcome.Failed(record, record.Error);
            }

            Directory.CreateDirectory(directory);

            if (File.Exists(target))
            {
                target = NextFreePath(directory, record.FileName);
            }

            await Task.Run(() =>
            {
                if (Path.GetPathRoot(target)!.Equals(Path.GetPathRoot(record.ArchivedPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(record.ArchivedPath!, target);
                }
                else
                {
                    File.Copy(record.ArchivedPath!, target);
                    File.Delete(record.ArchivedPath!);
                }
            }, cancellationToken).ConfigureAwait(false);

            record.RestoredPath = target;
            record.ArchivedPath = null;
            record.Status = ArchiveStatus.Reverted;
            record.Error = null;
            return ArchiveOutcome.Ok(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            record.Status = ArchiveStatus.Failed;
            record.Error = ex.Message;
            return ArchiveOutcome.Failed(record, ex.Message);
        }
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "未分类" : cleaned;
    }

    private static string NextFreePath(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static async Task CopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, useAsync: true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>长度 + 首 1MB 的 SHA-256：够判断"是不是同一个文件"，又不为几百 MB 的文件付全量哈希的代价。</summary>
    public static async Task<string> SampleHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        var length = stream.Length;
        var buffer = new byte[(int)Math.Min(length, HashSampleBytes)];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var hash = SHA256.HashData(buffer.AsSpan(0, offset));
        return length.ToString() + ":" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}