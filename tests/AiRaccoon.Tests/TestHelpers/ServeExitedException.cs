namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A serve run ended before printing what the test was waiting for, carrying its exit code so a
///     caller can tell a retryable outcome from a real failure without reading stderr.
/// </summary>
public sealed class ServeExitedException(int exitCode, string message) : InvalidOperationException(message)
{
    public int ExitCode { get; } = exitCode;
}
