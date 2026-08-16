namespace AiRaccoon.Settings;

/// <summary>
///     Which CLI command paths write the bank directly instead of through the server (ADR-0075
///     §5.3). Two only, and both are named here rather than left implicit:
///     <list type="bullet">
///         <item><c>encryption</c> — the bootstrap path: it creates and keys the bank before a
///         server can resolve a key and decrypt it (§5.1).</item>
///         <item><c>model set</c> — its progress/cancellation shape over HTTP is unruled (§10.3),
///         so it stays a CLI writer until that is designed; it is not exempt on principle, only
///         pending that decision.</item>
///     </list>
///     Any addition to this list is an addition to ADR-0075's exception table.
/// </summary>
internal static class CliWriteOptOuts
{
    internal static bool WritesDirectly(IReadOnlyList<string> commandPath) =>
        commandPath is ["encryption", ..] or ["model", "set", ..];
}
