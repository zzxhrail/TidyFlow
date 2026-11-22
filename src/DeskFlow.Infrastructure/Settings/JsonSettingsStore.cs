using System.Text.Json;
using System.Text.Json.Nodes;
using DeskFlow.Core.Abstractions;

namespace DeskFlow.Infrastructure.Settings;

/// <summary>%LOCALAPPDATA%\DeskFlow\settings.json，键值都走 JsonNode，加新配置不需要改存储代码。</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly JsonObject _root;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _root = Load(_path);
    }

    public T? Get<T>(string key, T? fallback = default)
    {
        if (!_root.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            return node.Deserialize<T>() ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public void Set<T>(string key, T value)
    {
        _root[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
        Save();
    }

    private static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).TrimStart('\uFEFF');   // 记事本另存会带 BOM
                return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
        }
        catch (Exception)
        {
            // 配置损坏时退回空配置，不阻塞启动。
        }

        return new JsonObject();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}