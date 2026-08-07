using AiRaccoon.Setup.Cli;

namespace AiRaccoon.Observability;

/// <summary>
///     Resolves the running serve process's PID and prints the monitoring command for the
///     requested kind (counters/trace/otlp/pid). Stub for P1 routing; P4 implements this.
/// </summary>
internal static class ObservabilityRunner
{
    public static Task<int> RunAsync(CliParseResult parsed, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        // P4 implements this
        return Task.FromResult(ExitCode.NoServerRunning);
    }
}
