using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;
using DeskFlow.Core.Runtime;

namespace DeskFlow.Infrastructure.Sinks;

/// <summary>
/// 默认输出：一份 UTF-8-BOM 的 CSV（Excel 双击不乱码）+ 一份 JSON（给程序读）。
/// 列 = 固定基础列 + 所有步骤写入的字段并集，新增步骤无需改这里。
/// </summary>
public sealed class ReportSink : IOutputSink
{
    private static readonly (string Key, string Title)[] BaseColumns =
    [
        ("path", "相对路径"),
        ("size", "字节"),
        ("modified", "修改时间"),
        ("status", "状态"),
        ("error", "错误"),
        ("notes", "备注"),
    ];

    public string Name => "report";

    public string Description => "输出 report.csv + report.json 汇总报表";

    public async Task<IReadOnlyList<Artifact>> WriteAsync(
        JobContext context,
        IReadOnlyList<ProcessedItem> items,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(context.OutputDirectory);

        var fieldKeys = items
            .SelectMany(static i => i.Fields.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var csvPath = Path.Combine(context.OutputDirectory, "report.csv");
        var jsonPath = Path.Combine(context.OutputDirectory, "report.json");

        var csv = new StringBuilder();
        var headerCells = BaseColumns.Select(static c => c.Title).Concat(fieldKeys).ToList();
        csv.AppendLine(string.Join(',', headerCells.Select(Escape)));

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cells = new List<string>(headerCells.Count)
            {
                item.Source.RelativePath,
                item.Source.SizeBytes.ToString(CultureInfo.InvariantCulture),
                item.Source.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                item.Failed ? "failed" : "ok",
                item.Error ?? string.Empty,
                string.Join("; ", item.Notes),
            };

            foreach (var key in fieldKeys)
            {
                cells.Add(item.Get(key) ?? string.Empty);
            }

            csv.AppendLine(string.Join(',', cells.Select(Escape)));
        }

        await File.WriteAllTextAsync(csvPath, csv.ToString(), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(
            new
            {
                runId = context.RunId,
                inputRoot = context.InputRoot,
                generatedAt = DateTimeOffset.UtcNow,
                itemCount = items.Count,
                failedCount = items.Count(static i => i.Failed),
                fields = fieldKeys,
                items = items.Select(static i => new
                {
                    path = i.Source.RelativePath,
                    fullPath = i.Source.FullPath,
                    size = i.Source.SizeBytes,
                    modifiedUtc = i.Source.LastWriteUtc,
                    status = i.Failed ? "failed" : "ok",
                    error = i.Error,
                    notes = i.Notes,
                    fields = i.Fields,
                }),
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

        await File.WriteAllTextAsync(jsonPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        return
        [
            new Artifact(csvPath, "csv", new FileInfo(csvPath).Length),
            new Artifact(jsonPath, "json", new FileInfo(jsonPath).Length),
        ];
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return text;
        }

        return '"' + text.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
