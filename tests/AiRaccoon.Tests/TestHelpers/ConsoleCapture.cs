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
}
