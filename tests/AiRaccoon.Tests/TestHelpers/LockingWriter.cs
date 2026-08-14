using System.Text;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>Thread-safe capture for a runner's stdout/stderr writers.</summary>
public sealed class LockingWriter : TextWriter
{
    private readonly StringBuilder _buffer = new();
    private readonly Lock _lock = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_lock)
        {
            _buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_lock)
        {
            _buffer.Append(value);
        }
    }

    /// <summary>Without this the base class falls back to one locked Write(char) per character.</summary>
    public override void Write(char[] buffer, int index, int count)
    {
        lock (_lock)
        {
            _buffer.Append(buffer, index, count);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            _buffer.AppendLine(value);
        }
    }

    /// <summary>Reads under the write lock; a StringWriter torn by a live writer throws instead.</summary>
    public string Snapshot() => ToString();

    public override string ToString()
    {
        lock (_lock)
        {
            return _buffer.ToString();
        }
    }
}
