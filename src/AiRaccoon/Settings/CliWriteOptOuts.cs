using AiRaccoon.Setup.Cli.Commands;

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
    internal static bool WritesDirectly(IReadOnlyList<string> commandPath) => commandPath is ["encryption", ..];
}

/// <summary>
///     The command-type twin of <see cref="CliWriteOptOuts" />, kept in the same file so both are
///     amended together: <c>CliCommandsDoNotOpenTheBankTests</c> derives every CLI command type from
///     <c>ConfigCommands</c>'s own constructor (no hand-listing there) and fails if any of them
///     resolves — anywhere in its constructed object graph — a live
///     <see cref="AiRaccoon.Infrastructure.Sqlite.ISqliteConnectionFactory" />. Three are allowed to:
///     <c>EncryptionCommands</c>, the same bootstrap exception <see cref="CliWriteOptOuts" /> already
///     names — it creates and keys the bank before a server can decrypt it; <c>DoctorCommands</c>,
///     which opens its own read-only connection and deliberately bypasses
///     <c>MemorySchema.EnsureAsync</c> (<c>DoctorCommands.cs:85-102</c>) rather than reading through
///     the server; and <c>ServeCommands</c>, which is not asking a server to do anything — running it
///     <em>is</em> becoming the server (<c>NodeRunner</c> probes the bank's own decryption key,
///     <c>NodeRunner.cs:107</c>, before binding the port it will serve from). The reflection test
///     found this third one on its first red run — the task that added it only named the first two —
///     which is exactly the point of deriving from the constructed graph instead of trusting a
///     hand-written list to already be complete. This list is still hand-maintained — three names is
///     as far as the derivation goes before a human has to say "these are the sanctioned
///     exceptions" — but it is three names instead of the unchecked list that let `repair --apply`,
///     `noise entries` and `watch registered` all open the bank directly without ever being added
///     anywhere.
/// </summary>
internal static class BankCapableCliCommandAllowlist
{
    internal static readonly IReadOnlyCollection<Type> Types =
    [
        typeof(EncryptionCommands),
        typeof(DoctorCommands),
        typeof(ServeCommands)
    ];
}
