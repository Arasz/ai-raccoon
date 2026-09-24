using System.Runtime.CompilerServices;

namespace AiRaccoon.Tests;

/// <summary>
///     Routes this test process's stderr, where every host and CLI console log line goes, to
///     <see cref="FilePath" />. <c>ConsoleCapture</c> still swaps and restores it for tests that assert on output.
/// </summary>
internal static class TestConsoleLog
{
    /// <summary>The log file beside the test assembly, recreated on every run.</summary>
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "test-stderr.log");

    [ModuleInitializer]
    public static void Initialize() =>
        Console.SetError(new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        });
}
