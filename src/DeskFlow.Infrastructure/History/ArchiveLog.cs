using System.Text;
using System.Text.Json;

namespace DeskFlow.Infrastructure.History;

/// <summary>一条归档动作记录（只追加，不修改历史）。</summary>
public sealed record ArchiveLogEntry(
    DateTimeOffset Time,
    string Action,
    string FileName,
    string? Category,
    string? Source,
    string From,
    string To);

/// <summary>
/// 归档日志：每次归档/撤销都追加一行 JSON，落在
/// %LOCALAPPDATA%\DeskFlow\archive-log.jsonl。
/// 索引里也有原位置，但日志是"追加式"的——即使记录被撤销、覆盖，
/// 也能查到"什么时候、把什么、从哪搬到哪"。
/// </summary>
public sealed class ArchiveLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public ArchiveLog(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.RootDirectory, "archive-log.jsonl");
    }

    public string Path_ => _path;

    public void Write(ArchiveLogEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, json + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public void Archive(string fileName, string? category, string? source, string from, string to) =>
        Write(new ArchiveLogEntry(DateTimeOffset.Now, "archive", fileName, category, source, from, to));

    public void Undo(string fileName, string? category, string? source, string from, string to) =>
        Write(new ArchiveLogEntry(DateTimeOffset.Now, "undo", fileName, category, source, from, to));

    /// <summary>读最近 N 条（文件可能很大，按行倒着取）。</summary>
    public IReadOnlyList<ArchiveLogEntry> Tail(int count = 200)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var entries = new List<ArchiveLogEntry>();
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<ArchiveLogEntry>(line);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return entries.TakeLast(count).Reverse().ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }
}
