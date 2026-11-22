using System.Text;
using DeskFlow.Core.Abstractions;
using DeskFlow.Core.Models;

namespace DeskFlow.Infrastructure.Observers;

/// <summary>把引擎日志写到一个 TextWriter（控制台或文件），带时间戳与等级。</summary>
public sealed class TextWriterObserver : IJobObserver, IDisposable
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();

    public TextWriterObserver(TextWriter writer, bool ownsWriter = false)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
    }

    public static TextWriterObserver ForFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 8192, useAsync: false);
        return new TextWriterObserver(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }, ownsWriter: true);
    }

    public void Log(JobLogLevel level, string message)
    {
        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}");
        }
    }

    public void ReportProgress(JobProgress progress)
    {
        lock (_gate)
        {
            _writer.WriteLine($"        progress {progress.Completed}/{progress.Total} ({progress.Percent}%) {progress.Phase}");
        }
    }

    public void Dispose()
    {
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
    }
}
