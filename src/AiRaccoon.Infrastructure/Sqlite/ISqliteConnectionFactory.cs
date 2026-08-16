using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

public interface ISqliteConnectionFactory
{
    string BankPath { get; }
    Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rekeys a bank still encrypted under the pre-ADR-0012 derivation to the HKDF key (ADR-0012).
    ///     No-op when the bank already opens under the current key; refuses unless the legacy key both
    ///     opens the bank and passes quick_check (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 2).
    /// </summary>
    /// <returns>True when the bank was rekeyed; false when it already opened under the current key.</returns>
    Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Opens with an already-resolved key. A bank still under the pre-ADR-0012 derivation is
    ///     reported as such instead of surfacing a bare "file is not a database"; the open path
    ///     never rekeys (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 3).
    /// </summary>
    Task<SqliteConnection> OpenBankWithResolvedKeyAsync(ResolvedKey resolvedKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rekeys the bank to a new key (raw x'…' or passphrase) on a DELETE-journal connection —
    ///     WAL rekey risks corruption or a stale key salt on this SQLCipher build
    ///     (docs/plans/encryption-bitwarden-implementation.md) — then verifies by reopening. The
    ///     current-key pool is drained first; callers must not hold an open bank connection.
    /// </summary>
    Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default);

    /// <summary>
    ///     As <see cref="SqliteConnectionFactory.RekeyBankAsync(string,System.Threading.CancellationToken)" />, but with the bank's current
    ///     key given explicitly — the migration's current key is the legacy derivation, which is
    ///     precisely the key the resolver no longer returns.
    /// </summary>
    Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default);

    /// <summary>Opens the bank with an explicit key (null = unencrypted): pragmas, vec0, schema.</summary>
    Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Opens the bank without <c>MemorySchema.EnsureAsync</c>'s per-open schema check — for a hot,
    ///     pre-every-call read (ToolGate's migration check, ADR-0076) that does not need it repeated.
    ///     Callers must compare <c>PRAGMA application_id</c> against <c>MemorySchema.SchemaDigest</c>
    ///     themselves and fall back to <c>MemorySchema.EnsureAsync</c> on the same connection when it
    ///     does not match.
    /// </summary>
    Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default);
}
