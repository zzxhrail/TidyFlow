using System.Text;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Infrastructure.Steps;

/// <summary>
/// 生成"建议文件名"：全角转半角、去非法字符、压缩空格、限制长度。
/// 只写字段不落盘，真正改名由后续 RenameSink（未启用）决定，避免误伤原文件。
/// </summary>
public sealed class NormalizeNameStep : IPipelineStep
{
    private static readonly char[] IllegalChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public string Name => "normalize-name";

    public string Description => "生成规范化的建议文件名（不修改磁盘文件）";

    public Task<StepOutcome> ExecuteAsync(ProcessedItem item, JobContext context, CancellationToken cancellationToken)
    {
        var original = item.Source.FileName;
        var extension = Path.GetExtension(original);
        var stem = Path.GetFileNameWithoutExtension(original);

        var builder = new StringBuilder(stem.Length);
        foreach (var ch in stem.Normalize(NormalizationForm.FormKC))
        {
            if (ch < 32 || IllegalChars.Contains(ch))
            {
                builder.Append('_');
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(ch);
        }

        var normalized = builder.ToString();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        normalized = normalized.Trim().TrimEnd('.');
        if (normalized.Length == 0)
        {
            normalized = "unnamed";
        }

        if (ReservedNames.Contains(normalized))
        {
            normalized = "_" + normalized;
        }

        const int maxStem = 120;
        if (normalized.Length > maxStem)
        {
            normalized = normalized[..maxStem];
        }

        var suggested = normalized + extension.ToLowerInvariant();
        item.Set("name.suggested", suggested);
        item.Set("name.changed", string.Equals(suggested, original, StringComparison.Ordinal) ? "no" : "yes");

        return Task.FromResult(
            string.Equals(suggested, original, StringComparison.Ordinal)
                ? StepOutcome.Success()
                : StepOutcome.Success($"建议改名: {suggested}"));
    }
}
