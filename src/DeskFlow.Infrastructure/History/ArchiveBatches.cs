using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.History;

/// <summary>一次批量归档：同一类别、同一批次的一堆文件。</summary>
public sealed record ArchiveBatch(
    string Key,
    string Category,
    int Count,
    DateTimeOffset Latest,
    IReadOnlyList<FileRecord> Records)
{
    public string Display =>
        $"{Category} · {Count} 个文件 · {Latest.LocalDateTime:MM-dd HH:mm}";

    // 自定义 ComboBox 模板直接渲染条目对象，覆写 ToString 保证下拉框显示的是人话
    public override string ToString() => Display;
}

/// <summary>
/// 把归档记录按"批次"分组：优先用记录里的 BatchId；
/// 老记录没有 BatchId 时，按「同类别 + 同一分钟」兜底聚在一起。
/// </summary>
public static class ArchiveBatches
{
    public static IReadOnlyList<ArchiveBatch> Group(IEnumerable<FileRecord> records, int fallbackWindowSeconds = 60)
    {
        var batches = new List<ArchiveBatch>();
        var buckets = new Dictionary<string, List<FileRecord>>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            var time = record.ArchivedAt ?? record.DetectedAt;
            var key = string.IsNullOrWhiteSpace(record.BatchId)
                ? $"legacy|{record.Category}|{time.ToUnixTimeSeconds() / Math.Max(1, fallbackWindowSeconds)}"
                : "batch|" + record.BatchId;

            if (!buckets.TryGetValue(key, out var list))
            {
                list = [];
                buckets[key] = list;
            }

            list.Add(record);
        }

        foreach (var pair in buckets)
        {
            var list = pair.Value;
            var latest = list.Max(static r => r.ArchivedAt ?? r.DetectedAt);
            batches.Add(new ArchiveBatch(
                pair.Key,
                list[0].Category,
                list.Count,
                latest,
                list.OrderByDescending(static r => r.ArchivedAt ?? r.DetectedAt).ToList()));
        }

        return batches.OrderByDescending(static b => b.Latest).ToList();
    }
}