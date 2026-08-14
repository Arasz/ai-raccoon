using System.Text;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Runs an action with Console.Out/Console.Error redirected into buffers, restoring both in a
///     finally. Takes no lock of its own: callers serialize themselves, either by sitting in a
///     DisableParallelization collection or by holding TestData.EnvVarGate across the redirect.
/// </summary>
public static class ConsoleCapture
{
    /// <summary>Captures everything the action writes to stdout and stderr.</summary>
    public static (string Out, string Err) Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var stdout = new LockingWriter();
        var stderr = new LockingWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (stdout.Snapshot(), stderr.Snapshot());
    }

    /// <summary>Captures everything the action writes to stdout and stderr.</summary>
    public static async Task<(string Out, string Err)> RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var stdout = new LockingWriter();
        var stderr = new LockingWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (stdout.Snapshot(), stderr.Snapshot());
    }

    /// <summary>
    ///     Console wraps a writer passed to SetOut in a synchronized decorator, so writes are already
    ///     serialized; reading the buffer is not, and a StringWriter torn by a still-writing
    ///     background thread throws out of ToString(). Snapshot takes the same lock the writes do.
    /// </summary>
    private sealed class LockingWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly Lock _gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                _buffer.Append(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
            {
                _buffer.Append(buffer, index, count);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _buffer.AppendLine(value);
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return _buffer.ToString();
            }
        }
    }
}
