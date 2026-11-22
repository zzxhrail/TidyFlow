namespace DeskFlow.Core.Abstractions;

/// <summary>轻量配置存储。当前用 JSON 文件，将来换成注册表或数据库不影响上层。</summary>
public interface ISettingsStore
{
    T? Get<T>(string key, T? fallback = default);

    void Set<T>(string key, T value);
}
