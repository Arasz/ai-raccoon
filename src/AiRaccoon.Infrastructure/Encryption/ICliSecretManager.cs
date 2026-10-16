namespace AiRaccoon.Infrastructure.Encryption;

/// <summary>Outcome of a secret-CLI process invocation.</summary>
public sealed record CliSecretResult(int ExitCode, string Stdout, string Stderr)
{
    public string FirstErrorLine =>
        Stderr.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "(no stderr)";
}

/// <summary>
///     Runs a secret-manager CLI (bws or op). Injectable so callers can substitute a fake runner, and the
///     executable path is constructor-injectable so tests can point at an absolute fake path (no PATH mutation).
/// </summary>
public interface ICliSecretManager
{
    /// <summary>
    ///     Runs the tool with <c>[args]</c> without blocking a thread on the child's exit or output — every
    ///     await is a real async wait (Process.WaitForExitAsync, awaited stream reads), because this
    ///     runs on the bank-open path (.NET-F2). A given token is set as the child's token env var (BWS_ACCESS_TOKEN or OP_SERVICE_ACCOUNT_TOKEN)
    ///     — never as an argument, which any same-user process could read off argv — overriding
    ///     whatever the child would otherwise inherit from the environment.
    /// </summary>
    Task<CliSecretResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
