using System.Text;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Infrastructure.Steps;

/// <summary>文本探测：编码、行数、字符数。非文本文件直接跳过并记录类型，不算失败。</summary>
public sealed class TextProbeStep : IPipelineStep
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".log", ".xml", ".yml", ".yaml",
        ".ini", ".conf", ".config", ".cs", ".py", ".js", ".ts", ".html", ".htm", ".css", ".sql",
        ".ps1", ".bat", ".cmd", ".sh", ".java", ".go", ".rs", ".c", ".h", ".cpp", ".srt",
    };

    private const int ProbeBytes = 1024 * 1024;

    public string Name => "text-probe";

    public string Description => "识别文本编码、行数、字符数（非文本文件跳过）";

    public async Task<StepOutcome> ExecuteAsync(ProcessedItem item, JobContext context, CancellationToken cancellationToken)
    {
        if (!TextExtensions.Contains(item.Source.Extension))
        {
            item.Set("text.kind", "binary");
            return StepOutcome.Success();
        }

        try
        {
            var length = (int)Math.Min(item.Source.SizeBytes, ProbeBytes);
            var buffer = new byte[length];
            await using (var stream = new FileStream(item.Source.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true))
            {
                var offset = 0;
                while (offset < length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset < length)
                {
                    buffer = buffer[..offset];
                }
            }

            var (encoding, bomLength) = DetectEncoding(buffer);
            var text = encoding.GetString(buffer, bomLength, buffer.Length - bomLength);
            var lines = text.Length == 0 ? 0 : text.Count(static c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);

            item.Set("text.kind", "text");
            item.Set("text.encoding", encoding.WebName);
            item.Set("text.lines", lines.ToString());
            item.Set("text.chars", text.Length.ToString());
            item.Set("text.truncated", item.Source.SizeBytes > ProbeBytes ? "yes" : "no");
            return StepOutcome.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StepOutcome.Failure(ex.Message);
        }
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] buffer)
    {
        if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            return (new UTF8Encoding(false), 3);
        }

        if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            return (Encoding.Unicode, 2);
        }

        if (buffer.Length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2);
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            // 无法判定时按 UTF-8 宽松解码，只标注 low-confidence。
            return (new UTF8Encoding(false, throwOnInvalidBytes: false), 0);
        }
    }
}
