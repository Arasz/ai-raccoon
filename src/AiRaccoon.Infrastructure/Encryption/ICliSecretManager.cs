namespace AiRaccoon.Infrastructure.Encryption;

/// <summary>Outcome of a bws process invocation.</summary>
public sealed record BwsResult(int ExitCode, string Stdout, string Stderr)
{
    public string FirstErrorLine =>
        Stderr.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "(no stderr)";
}

/// <summary>
///     Runs the bws CLI. Injectable so callers can substitute a fake runner, and the executable
///     path is constructor-injectable so tests can point at an absolute fake-bws path (no PATH mutation).
/// </summary>
public interface ICliSecretManager
{
    /// <summary>
    ///     Runs <c>bws [args]</c>. A given token is set as the child's BWS_ACCESS_TOKEN — never
    ///     as an argument, which any same-user process could read off argv — overriding whatever
    ///     the child would otherwise inherit from the environment.
    /// </summary>
    BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout);
}
