using System.Text.Json;
using System.Text.Json.Serialization;
using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Index;

/// <summary>
/// 归档索引（JSON 文件，原子写入）。记录所有走过来的文件，面板的"最近文件/待接收"都从它来。
/// 数据量级是几万条以内，用 JSON 足够；真到十万级再换 SQLite，接口不变。
/// </summary>
public sealed class JsonArchiveIndex
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly List<FileRecord> _records = new();
    private readonly object _gate = new();

    public JsonArchiveIndex(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.RootDirectory, "archive-index.json");
        Load();
    }

    public IReadOnlyList<FileRecord> All
    {
        get
        {
            lock (_gate)
            {
                return _records.ToList();
            }
        }
    }

    public IReadOnlyList<FileRecord> Recent(int count)
    {
        lock (_gate)
        {
            return _records
                .Where(static r => r.Status == ArchiveStatus.Archived)
                .OrderByDescending(static r => r.ArchivedAt ?? r.DetectedAt)
                .Take(count)
                .ToList();
        }
    }

    public IReadOnlyList<FileRecord> Pending()
    {
        lock (_gate)
        {
            return _records.Where(static r => r.Status == ArchiveStatus.Pending).OrderByDescending(static r => r.DetectedAt).ToList();
        }
    }

    public bool KnowsSourcePath(string path)
    {
        lock (_gate)
        {
            return _records.Any(r => string.Equals(r.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Upsert(FileRecord record)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.Id == record.Id);
            if (index >= 0)
            {
                _records[index] = record;
            }
            else
            {
                _records.Add(record);
            }
        }

        Save();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => r.Id == id);
        }

        Save();
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            _records.RemoveAll(static r => r.Status == ArchiveStatus.Ignored);
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<FileRecord>>(json, SerializerOptions);
            if (loaded is not null)
            {
                _records.AddRange(loaded);
            }
        }
        catch (Exception)
        {
            // 索引损坏时从空开始，不影响主流程。
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_records, SerializerOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}