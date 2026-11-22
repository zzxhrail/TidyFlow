namespace DeskFlow.Core.Models;

/// <summary>
/// 处理管道里流动的可变载体。每个步骤只往 Fields / Notes 里写，不改动磁盘上的原始文件。
/// 这样任何一步失败都不会造成半成品，且整个管道可重放。
/// </summary>
public sealed class ProcessedItem
{
    public ProcessedItem(InputItem source) => Source = source;

    public InputItem Source { get; }

    public Dictionary<string, string?> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Notes { get; } = new();

    public bool Failed { get; set; }

    public string? Error { get; set; }

    public string? Get(string key) => Fields.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string? value) => Fields[key] = value;

    public void Note(string text) => Notes.Add(text);
}
