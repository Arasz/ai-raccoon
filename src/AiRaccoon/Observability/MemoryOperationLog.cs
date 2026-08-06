using System.Text.Json;

namespace AiRaccoon.Observability;

/// <summary>
///     Append-only JSONL record of memory tool invocations (the analysis data source).
///     One row per invocation: ts, tool, project_id, status, error_type?, duration_ms.
/// </summary>
public sealed class MemoryOperationLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public MemoryOperationLog(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Write(string tool, string? projectId, TimeSpan duration, bool isError, string? errorType = null)
    {
        var row = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["tool"] = tool,
            ["project_id"] = projectId,
            ["status"] = isError ? "error" : "ok"
        };
        if (isError && errorType is not null)
        {
            row["error_type"] = errorType;
        }

        row["duration_ms"] = Math.Round(duration.TotalMilliseconds, 1);

        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(row));
        }
    }

    public void Dispose() => _writer.Dispose();
}
