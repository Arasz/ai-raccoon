namespace AiRaccoon.Settings;

/// <summary>
///     Which CLI command paths write the bank directly instead of through the server (ADR-0075
///     §5.3). One only, named here rather than left implicit: <c>encryption</c> is the bootstrap
///     path — it creates and keys the bank before a server can resolve a key and decrypt it (§5.1).
///     <c>model set</c> was here too until ADR-0076 ruled §10.3: it routes through the server like
///     every other settings command now (the outbox commits fast; the re-embed itself never blocked
///     this decision).
///     Any addition to this list is an addition to ADR-0075's exception table.
/// </summary>
internal static class CliWriteOptOuts
{
    internal static bool WritesDirectly(IReadOnlyList<string> commandPath) =>
        commandPath is ["encryption", ..];
}
