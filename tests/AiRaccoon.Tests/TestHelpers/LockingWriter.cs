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

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            _buffer.AppendLine(value);
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return _buffer.ToString();
        }
    }
}
