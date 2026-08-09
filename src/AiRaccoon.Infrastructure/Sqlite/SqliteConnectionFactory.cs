using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Opens the install's single memory bank (memory.db) with the shared PRAGMA policy, loads
///     vec0 (NuGet), and initializes our schema on first open. There is no second meta database:
///     every table lives in memory.db (FR-NM-1; see docs/work/features-native-memory/native-memory.feature).
/// </summary>
public sealed class SqliteConnectionFactory(InfrastructureOptions options, IEncryptionKeyResolver keyResolver)
{
    static SqliteConnectionFactory()
    {
        // Dapper maps columns to constructor parameters case-insensitively but not across
        // underscores; our schema uses snake_case (created_at, access_count, …).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public string BankPath => BankPathFor(options);

    /// <summary>Directory holding the bank: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon for project scope.</summary>
    private static string BankDirectoryFor(InfrastructureOptions options) =>
        options.Scope switch
        {
            InstallScope.User => options.DataRoot,
            InstallScope.Project => Path.Combine(options.DataRoot, ".ai-raccoon"),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Scope), options.Scope,
                "Unknown install scope.")
        };

    /// <summary>The bank path for the given options; shared by the factory and the source resolver.</summary>
    public static string BankPathFor(InfrastructureOptions options) => Path.Combine(BankDirectoryFor(options), "memory.db");

    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) =>
        await OpenBankWithResolvedKeyAsync(keyResolver.Resolve(), cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     Rekeys a bank still encrypted under the pre-ADR-0012 derivation to the HKDF key (ADR-0012).
    ///     No-op when the bank already opens under the current key; refuses unless the legacy key both
    ///     opens the bank and passes quick_check (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 2).
    /// </summary>
    /// <returns>True when the bank was rekeyed; false when it already opened under the current key.</returns>
    public async Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default)
    {
        var resolvedKey = keyResolver.Resolve();

        SqliteConnection connection;
        try
        {
            connection = await OpenConnectionAsync(resolvedKey.Passphrase, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException openFailure)
        {
            if (resolvedKey.LegacyPassphrase is null || resolvedKey.Passphrase is null)
            {
                throw new BankKeyMismatchException(
                    $"the bank at '{BankPath}' did not open with the {resolvedKey.SourceName} encryption key, and that source has no earlier key derivation to fall back to",
                    openFailure);
            }

            if (!await LegacyKeyOpensHealthyBankAsync(resolvedKey.LegacyPassphrase, cancellationToken).ConfigureAwait(false))
            {
                throw NotLegacyKeyed(resolvedKey, openFailure);
            }

            // Only reached with positive proof that the legacy key opens a healthy bank.
            await RekeyBankAsync(resolvedKey.Passphrase!, resolvedKey.LegacyPassphrase!, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // The current key already opened the bank — that alone proves there is nothing to
        // migrate. Dispose the bare probe connection without ever calling InitializeAsync: this
        // path must not run MemorySchema.EnsureAsync, or "nothing to do" would be a lie for a
        // bank that had no schema yet.
        await connection.DisposeAsync().ConfigureAwait(false);
        return false;
    }

    /// <summary>
    ///     Opens with an already-resolved key. A bank still under the pre-ADR-0012 derivation is
    ///     reported as such instead of surfacing a bare "file is not a database"; the open path
    ///     never rekeys (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 3).
    /// </summary>
    public async Task<SqliteConnection> OpenBankWithResolvedKeyAsync(ResolvedKey resolvedKey,
        CancellationToken cancellationToken = default)
    {
        SqliteConnection connection;
        try
        {
            connection = await OpenConnectionAsync(resolvedKey.Passphrase, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException openFailure) when (resolvedKey.LegacyPassphrase is not null)
        {
            throw await DiagnoseAsync(resolvedKey, openFailure, cancellationToken).ConfigureAwait(false);
        }

        // Post-open failures (extensions, vector load, schema DDL) are not key-related and
        // must propagate unchanged — only the open above proves whether the key is wrong.
        return await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rekeys the bank to a new key (raw x'…' or passphrase) on a DELETE-journal connection —
    ///     WAL rekey risks corruption or a stale key salt on this SQLCipher build
    ///     (docs/plans/encryption-bitwarden-implementation.md) — then verifies by reopening. The
    ///     current-key pool is drained first; callers must not hold an open bank connection.
    /// </summary>
    public Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default) =>
        RekeyBankAsync(newKey, keyResolver.Resolve().Passphrase, cancellationToken);

    /// <summary>
    ///     As <see cref="RekeyBankAsync(string,CancellationToken)" />, but with the bank's current
    ///     key given explicitly — the migration's current key is the legacy derivation, which is
    ///     precisely the key the resolver no longer returns.
    /// </summary>
    public async Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(newKey);

        SqliteConnection.ClearPool(new SqliteConnection(BuildConnectionString(currentKey)));

        await using (var connection = await OpenRekeyConnectionAsync(currentKey, cancellationToken).ConfigureAwait(false))
        {
            // quote() produces the same literal form Microsoft.Data.Sqlite uses for Password — it
            // escapes both raw x'…' keys and passphrases (docs/plans/encryption-bitwarden-implementation.md).
            await using var quoteCommand = connection.CreateCommand();
            quoteCommand.CommandText = "SELECT quote($newKey)";
            quoteCommand.Parameters.AddWithValue("$newKey", newKey);
            var quoted = (string)(await quoteCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            await using var rekeyCommand = connection.CreateCommand();
            rekeyCommand.CommandText = $"PRAGMA rekey = {quoted}";
            await rekeyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Verify: the bank must reopen with the new key, or the rekey did not land.
        await using var verify = await OpenBankWithKeyAsync(newKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns a failed open into the specific reason, without ever writing to the bank.</summary>
    private async Task<BankKeyMismatchException> DiagnoseAsync(ResolvedKey resolvedKey, SqliteException openFailure,
        CancellationToken cancellationToken)
    {
        if (!await LegacyKeyOpensHealthyBankAsync(resolvedKey.LegacyPassphrase!, cancellationToken).ConfigureAwait(false))
        {
            return NotLegacyKeyed(resolvedKey, openFailure);
        }

        return new BankKeyMismatchException(
            $"the bank at '{BankPath}' is still encrypted under the pre-ADR-0012 key derivation — run 'ai-raccoon encryption migrate' to rekey it",
            openFailure);
    }

    private BankKeyMismatchException NotLegacyKeyed(ResolvedKey resolvedKey, SqliteException openFailure) =>
        new($"the bank at '{BankPath}' opens under neither the current nor the pre-ADR-0012 {resolvedKey.SourceName} key derivation — "
            + "it is corrupt, or keyed to a different secret. It has not been modified; restore it from a backup or check that the encryption source is right.",
            openFailure);

    /// <summary>
    ///     True only when the legacy key opens the bank <em>and</em> quick_check reports "ok" — the
    ///     sole thing that authorises a rekey. Read-only and pool-free, so a refusal leaves the file
    ///     untouched; any SQLite failure means "no proof", never "corrupt" specifically (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 2).
    /// </summary>
    private async Task<bool> LegacyKeyOpensHealthyBankAsync(string legacyKey, CancellationToken cancellationToken)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = BankPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = legacyKey,
            Pooling = false
        };

        try
        {
            await using var connection = new SqliteConnection(csb.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

            return string.Equals(result, "ok", StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Opens the bank with an explicit key (null = unencrypted): pragmas, vec0, schema.</summary>
    public async Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(key, cancellationToken).ConfigureAwait(false);
        return await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the connection under the given key (null = unencrypted) with the shared
    /// pragmas — the step that fails on a wrong key.</summary>
    private async Task<SqliteConnection> OpenConnectionAsync(string? key, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(BankDirectoryFor(options));

        var connection = new SqliteConnection(BuildConnectionString(key));
        await OpenWithPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Loads vec0 and ensures the schema on an already-open connection. Disposes and
    /// rethrows on failure so a failed post-open step never leaks a pooled, checked-out
    /// connection.</summary>
    internal static async Task<SqliteConnection> InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            connection.EnableExtensions();
            // vec0 ships in the NuGet package — always available, no provisioning.
            connection.LoadVector();
            await MemorySchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string BuildConnectionString(string? key)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = BankPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        if (key is not null)
        {
            csb.Password = key;
        }

        return csb.ToString();
    }

    private async Task<SqliteConnection> OpenRekeyConnectionAsync(string? key, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(BankDirectoryFor(options));

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = BankPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        if (key is not null)
        {
            csb.Password = key;
        }

        var connection = new SqliteConnection(csb.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=DELETE";
        await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private static async Task OpenWithPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var fk = connection.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys = ON";
        await fk.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
