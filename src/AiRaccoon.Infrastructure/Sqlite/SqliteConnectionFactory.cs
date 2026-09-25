using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using System.Runtime.CompilerServices;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Opens the install's single memory bank (memory.db) with the shared PRAGMA policy, loads
///     vec0 (NuGet), and initializes our schema on first open. There is no second meta database:
///     every table lives in memory.db (FR-NM-1; see docs/work/features-native-memory/native-memory.feature).
/// </summary>
public sealed partial class SqliteConnectionFactory(
    InfrastructureOptions options,
    IEncryptionKeyResolver keyResolver,
    ILogger<SqliteConnectionFactory>? logger = null) : ISqliteConnectionFactory
{
    /// <summary>
    ///     Native handles this process has initialised, with the bank state their last schema pass saw. The pool hands a handle back on every open; reloading vec0 (a dlopen) and repeating
    ///     the schema pass dominated the idle reconcile loop's CPU. Weak keys: a handle the pool closes
    ///     drops out on its own.
    /// </summary>
    private static readonly ConditionalWeakTable<sqlite3, StrongBox<BankState>> InitializedHandles = new();

    private const string BankStateSql =
        "SELECT (SELECT data_version FROM pragma_data_version) AS DataVersion, " +
        "(SELECT schema_version FROM pragma_schema_version) AS SchemaVersion, " +
        "(SELECT user_version FROM pragma_user_version) AS UserVersion, " +
        "(SELECT application_id FROM pragma_application_id) AS ApplicationId";

    /// <summary>What the schema pass's work depends on; equal values mean it would find nothing to do.</summary>
    private readonly record struct BankState(long DataVersion, long SchemaVersion, long UserVersion, long ApplicationId);

    /// <summary>SQLITE_NOTADB (26): the file exists but is not a database (or not this key's database).</summary>
    private const int NotADatabaseErrorCode = 26;

    static SqliteConnectionFactory()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public string BankPath => BankPathFor(options);

    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) =>
        await OpenBankWithResolvedKeyAsync(
            await keyResolver.ResolveAsync(cancellationToken), cancellationToken);

    /// <summary>
    ///     Rekeys a bank still encrypted under the pre-ADR-0012 derivation to the HKDF key (ADR-0012).
    ///     No-op when the bank already opens under the current key; refuses unless the legacy key both
    ///     opens the bank and passes quick_check (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 2).
    /// </summary>
    /// <returns>True when the bank was rekeyed; false when it already opened under the current key.</returns>
    public async Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default)
    {
        var resolvedKey = await keyResolver.ResolveAsync(cancellationToken);

        SqliteConnection connection;
        try
        {
            connection = await OpenConnectionAsync(resolvedKey.Passphrase, cancellationToken);
        }
        catch (SqliteException openFailure)
        {
            if (resolvedKey.LegacyPassphrase is null || resolvedKey.Passphrase is null)
            {
                var classified = TryDiagnoseWithKeyCheck(resolvedKey.Passphrase, openFailure) ??
                    NoLegacyDerivationToFallBackTo(resolvedKey, openFailure);
                if (ReferenceEquals(classified, openFailure))
                {
                    throw;
                }

                throw classified;
            }

            if (!await LegacyKeyOpensHealthyBankAsync(resolvedKey.LegacyPassphrase, cancellationToken))
            {
                throw TryDiagnoseWithKeyCheck(resolvedKey.Passphrase, openFailure) ?? NotLegacyKeyed(resolvedKey, openFailure);
            }

            // Only reached with positive proof that the legacy key opens a healthy bank.
            await RekeyBankAsync(resolvedKey.Passphrase!, resolvedKey.LegacyPassphrase!, cancellationToken);
            return true;
        }

        // The current key already opened the bank — that alone proves there is nothing to
        // migrate. Dispose the bare probe connection without ever calling InitializeAsync: this
        // path must not run MemorySchema.EnsureAsync, or "nothing to do" would be a lie for a
        // bank that had no schema yet.
        await connection.DisposeAsync();
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
            connection = await OpenConnectionAsync(resolvedKey.Passphrase, cancellationToken);
        }
        catch (SqliteException openFailure)
        {
            if (resolvedKey.LegacyPassphrase is not null)
            {
                throw await DiagnoseAsync(resolvedKey, openFailure, cancellationToken);
            }

            var classified = ClassifyNoLegacySourceFailure(resolvedKey, openFailure);
            if (ReferenceEquals(classified, openFailure))
            {
                throw;
            }

            throw classified;
        }

        // Post-open failures (extensions, vector load, schema DDL) are not key-related and
        // must propagate unchanged — only the open above proves whether the key is wrong.
        return await InitializeAsync(connection, cancellationToken, logger);
    }

    /// <summary>
    ///     Rekeys the bank to a new key (raw x'…' or passphrase) on a DELETE-journal connection —
    ///     WAL rekey risks corruption or a stale key salt on this SQLCipher build
    ///     (docs/plans/encryption-bitwarden-implementation.md) — then verifies by reopening. The
    ///     current-key pool is drained first; callers must not hold an open bank connection.
    /// </summary>
    public async Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default)
    {
        var resolvedKey = await keyResolver.ResolveAsync(cancellationToken);
        await RekeyBankAsync(newKey, resolvedKey.Passphrase, cancellationToken);
    }

    /// <summary>
    ///     As <see cref="RekeyBankAsync(string,CancellationToken)" />, but with the bank's current
    ///     key given explicitly — the migration's current key is the legacy derivation, which is
    ///     precisely the key the resolver no longer returns.
    /// </summary>
    public async Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(newKey);

        // Held across the whole rekey — PRAGMA rekey through the sidecar rewrite in the verify
        // reopen below — so a concurrent opener still on the old key that hits this exact window
        // is diagnosed as ambiguous, never confidently "corrupt" (ADR-0111 D6, issue #705).
        var marker = new RekeyMarker(BankPath);
        marker.Mark();
        try
        {
            SqliteConnection.ClearPool(new SqliteConnection(BuildConnectionString(currentKey)));

            await using (var connection = await OpenRekeyConnectionAsync(currentKey, cancellationToken))
            {
                // quote() produces the same literal form Microsoft.Data.Sqlite uses for Password — it
                // escapes both raw x'…' keys and passphrases (docs/plans/encryption-bitwarden-implementation.md).
                await using var quoteCommand = connection.CreateCommand();
                quoteCommand.CommandText = "SELECT quote($newKey)";
                quoteCommand.Parameters.AddWithValue("$newKey", newKey);
                var quoted = (string)(await quoteCommand.ExecuteScalarAsync(cancellationToken))!;

                await using var rekeyCommand = connection.CreateCommand();
                rekeyCommand.CommandText = $"PRAGMA rekey = {quoted}";
                await rekeyCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            // Verify: the bank must reopen with the new key, or the rekey did not land. The sidecar
            // rewrite this reopen triggers (EnsureKeyCheck) is what closes the window the marker guards.
            await using var verify = await OpenBankWithKeyAsync(newKey, cancellationToken);
        }
        finally
        {
            marker.Clear();
        }
    }

    /// <summary>
    ///     Opens the bank with an explicit key (null = unencrypted): pragmas, vec0, schema. Unlike
    ///     <see cref="OpenBankWithResolvedKeyAsync" />, this has no resolver context (no legacy
    ///     derivation to try) — a failed open still gets the key-check sidecar's verdict when one
    ///     is available (ADR-0111), else the raw <see cref="SqliteException" /> unchanged.
    /// </summary>
    public async Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default)
    {
        SqliteConnection connection;
        try
        {
            connection = await OpenConnectionAsync(key, cancellationToken);
        }
        catch (SqliteException openFailure)
        {
            throw TryDiagnoseWithKeyCheck(key, openFailure) ?? openFailure;
        }

        return await InitializeAsync(connection, cancellationToken, logger);
    }

    /// <inheritdoc cref="ISqliteConnectionFactory.OpenBankSkippingEnsureAsync" />
    public async Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default)
    {
        var resolvedKey = await keyResolver.ResolveAsync(cancellationToken);
        var connection = await OpenConnectionAsync(resolvedKey.Passphrase, cancellationToken);
        try
        {
            // Free (no SQL statement) — only exercised if the caller falls back to EnsureAsync,
            // which may CREATE VIRTUAL TABLE ... vec0(...) and needs the extension loaded first.
            connection.EnableExtensions();
            connection.LoadVector();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Directory holding the bank: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon for project scope.</summary>
    private static string BankDirectoryFor(InfrastructureOptions options) => BankPaths.DirectoryFor(options);

    /// <summary>The bank path for the given options; shared by the factory and the source resolver.</summary>
    public static string BankPathFor(InfrastructureOptions options) => Path.Combine(BankDirectoryFor(options), "memory.db");

    /// <summary>Turns a failed open into the specific reason, without ever writing to the bank.</summary>
    private async Task<Exception> DiagnoseAsync(ResolvedKey resolvedKey, SqliteException openFailure,
        CancellationToken cancellationToken)
    {
        if (!await LegacyKeyOpensHealthyBankAsync(resolvedKey.LegacyPassphrase!, cancellationToken))
        {
            return TryDiagnoseWithKeyCheck(resolvedKey.Passphrase, openFailure) ?? NotLegacyKeyed(resolvedKey, openFailure);
        }

        return new BankKeyMismatchException(
            $"the bank at '{BankPath}' is still encrypted under the pre-ADR-0012 key derivation — run 'ai-raccoon encryption migrate' to rekey it",
            openFailure, legacyDerivation: true);
    }

    private Exception NotLegacyKeyed(ResolvedKey resolvedKey, SqliteException openFailure) =>
        IsKeyMismatchShape(resolvedKey, openFailure)
            ? new BankKeyMismatchException(
                $"the bank at '{BankPath}' opens under neither the current nor the pre-ADR-0012 {resolvedKey.SourceName} key derivation — {NoOtherKeyToTryRemedy}",
                openFailure)
            : openFailure;

    /// <summary>
    ///     The migrate verb's own no-legacy-source message — "fall back to" the migration it cannot
    ///     attempt, not "try" the open already attempted — same classification rule as
    ///     <see cref="NoLegacyDerivationToTry" />.
    /// </summary>
    private Exception NoLegacyDerivationToFallBackTo(ResolvedKey resolvedKey, SqliteException openFailure) =>
        IsKeyMismatchShape(resolvedKey, openFailure)
            ? new BankKeyMismatchException(
                $"the bank at '{BankPath}' did not open with the {resolvedKey.SourceName} encryption key, and that source has no earlier key derivation to fall back to — {NoOtherKeyToTryRemedy}",
                openFailure)
            : openFailure;

    /// <summary>The resolved key's source has no earlier derivation to try, so a failed open can only be this bank's own key mismatch — unless the key-check sidecar (ADR-0111) already gives a confident verdict.</summary>
    private Exception NoLegacyDerivationToTry(ResolvedKey resolvedKey, SqliteException openFailure) =>
        TryDiagnoseWithKeyCheck(resolvedKey.Passphrase, openFailure) ??
        (IsKeyMismatchShape(resolvedKey, openFailure)
            ? new BankKeyMismatchException(
                $"the bank at '{BankPath}' did not open with the {resolvedKey.SourceName} encryption key, and that source has no earlier key derivation to try — {NoOtherKeyToTryRemedy}",
                openFailure)
            : openFailure);

    /// <summary>
    ///     A source with no legacy derivation to try gets the specific key-mismatch diagnosis only
    ///     for SQLITE_NOTADB on a source that actually resolved a key — SQLCipher's signature for a
    ///     wrong key. Busy/locked, a genuinely corrupt unencrypted bank, and every other SQLite
    ///     failure are not key problems and must propagate unchanged, or the CLI misreports
    ///     Bank.Busy/Bank.Corrupted/Bank.OpenFailed as a key mismatch.
    /// </summary>
    internal Exception ClassifyNoLegacySourceFailure(ResolvedKey resolvedKey, SqliteException openFailure) =>
        NoLegacyDerivationToTry(resolvedKey, openFailure);

    /// <summary>
    ///     The one shape a wrong key and a genuine corruption share (ADR-0107 PC.0): SQLITE_NOTADB
    ///     on a source that actually resolved a key. Anything else — a different SqliteErrorCode
    ///     (SQLITE_BUSY, SQLITE_CANTOPEN, ...), or no key at all (an unencrypted bank has nothing to
    ///     be wrong about) — is not a key problem and must propagate unchanged; every ambiguous-
    ///     message builder above shares this one rule, the same one <see cref="OpenBankWithKeyAsync" />
    ///     already applies via its own <c>?? openFailure</c>.
    /// </summary>
    private static bool IsKeyMismatchShape(ResolvedKey resolvedKey, SqliteException openFailure) =>
        resolvedKey.Passphrase is not null && openFailure.SqliteErrorCode == NotADatabaseErrorCode;

    /// <summary>The remedy shared by every "the resolved key does not open the bank, and there is nothing else to try" message.</summary>
    private const string NoOtherKeyToTryRemedy =
        "it is corrupt, or keyed to a different secret. It has not been modified; restore it from a backup or check that the encryption source is right.";

    /// <summary>
    ///     A confident verdict from the key-check sidecar (ADR-0111) for a SQLITE_NOTADB open
    ///     failure, or null when the sidecar is absent or cannot be trusted — the caller then falls
    ///     back to its own ambiguous, both-causes diagnosis. Never used to short-circuit the legacy-
    ///     derivation check: a bank still under the pre-ADR-0012 key is not "wrong key", and that
    ///     detection always runs first.
    /// </summary>
    private Exception? TryDiagnoseWithKeyCheck(string? key, SqliteException openFailure)
    {
        if (key is null || openFailure.SqliteErrorCode != NotADatabaseErrorCode)
        {
            return null;
        }

        KeyCheckRecord? record;
        try
        {
            record = new KeyCheckSidecar(BankPath).Read();
        }
        catch (KeyCheckViolation)
        {
            return null; // cannot trust an unreadable/shared verifier — fall back to the ambiguous diagnosis
        }

        if (record is null)
        {
            return null;
        }

        if (!KeyCheckSidecar.Verifies(record, key))
        {
            return new BankKeyMismatchException(
                $"the bank at '{BankPath}' does not open with the resolved encryption key — its key verifier confirms this key is wrong; check the encryption key source",
                openFailure);
        }

        // The sidecar verifies this key, which normally proves the bank itself is damaged. But a
        // rekey in progress (ADR-0111 D6) may not have rewritten the sidecar for its new key yet —
        // this open could be hitting exactly that window, not a truly corrupt file — so fall back
        // to the caller's own ambiguous, both-causes diagnosis rather than confidently claiming
        // corruption (issue #705).
        return new RekeyMarker(BankPath).IsPresent
            ? null
            : new BankCorruptedException(
                $"the bank at '{BankPath}' is corrupt — its key verifier confirms the encryption key is right, so the file itself is damaged; restore it from a backup",
                openFailure);
    }

    /// <summary>
    ///     Verifies the key-check sidecar (ADR-0111) against the key that just opened the bank:
    ///     mints one silently when absent, rewrites it when it no longer matches (the rekey/migrate
    ///     path), and never fails the open itself — a diagnostic aid must not become an availability
    ///     risk. Unencrypted banks (null key) have nothing to verify.
    /// </summary>
    private void EnsureKeyCheck(string? key)
    {
        if (key is null)
        {
            return;
        }

        var sidecar = new KeyCheckSidecar(BankPath);
        try
        {
            var record = sidecar.Read();
            if (record is null)
            {
                sidecar.MintIfMissing(key);
                return;
            }

            if (!KeyCheckSidecar.Verifies(record, key))
            {
                sidecar.Rewrite(key);
                if (logger is not null)
                {
                    Log.KeyCheckRewritten(logger, BankPath);
                }
            }
        }
        catch (Exception ex) when (ex is KeyCheckViolation or IOException or UnauthorizedAccessException)
        {
            if (logger is not null)
            {
                Log.KeyCheckUnavailable(logger, BankPath, ex);
            }
        }
    }

    /// <summary>
    ///     True only when the legacy key opens the bank <em>and</em> quick_check reports "ok" — the
    ///     sole thing that authorises a rekey. Never creates, and pool-free, so a refusal leaves the
    ///     file as it found it; any SQLite failure means "no proof", never "corrupt" specifically (docs/plans/2026-08-07-hkdf-rekey-migration.md Decision 2).
    /// </summary>
    private async Task<bool> LegacyKeyOpensHealthyBankAsync(string legacyKey, CancellationToken cancellationToken)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = BankPath,
            // Not ReadOnly: a WAL bank needs a writable -shm to open. Not Create either — this probe
            // must never bring a bank into existence.
            Mode = SqliteOpenMode.ReadWrite,
            Password = legacyKey,
            Pooling = false
        };

        try
        {
            await using var connection = new SqliteConnection(csb.ToString());
            await connection.OpenAsync(cancellationToken);

            await using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = await check.ExecuteScalarAsync(cancellationToken) as string;

            return string.Equals(result, "ok", StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Opens the connection under the given key (null = unencrypted) with the shared
    ///     pragmas — the step that fails on a wrong key.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(string? key, CancellationToken cancellationToken)
    {
        BankOpenObservation.RecordOpenAttempt(BankPath);
        BankPaths.CreateDirectory(BankDirectoryFor(options));

        var connection = new SqliteConnection(BuildConnectionString(key));
        await OpenWithPragmasAsync(connection, cancellationToken);
        EnsureKeyCheck(key);
        return connection;
    }

    /// <summary>
    ///     Loads vec0 and ensures the schema on an already-open connection. Disposes and
    ///     rethrows on failure so a failed post-open step never leaks a pooled, checked-out
    ///     connection. The one caller with a logger (this method) reports every watch the
    ///     unconditional no-overlapping-watches prune step removed
    ///     (docs/work/2026-08-21-code-search-implementation-plan.md §4) and every project whose
    ///     resolution failed and was skipped (S8) — the migration itself stays silent and only
    ///     returns the data.
    /// </summary>
    internal static async Task<SqliteConnection> InitializeAsync(SqliteConnection connection,
        CancellationToken cancellationToken, ILogger<SqliteConnectionFactory>? logger = null)
    {
        try
        {
            var handle = connection.Handle!;
            var known = InitializedHandles.TryGetValue(handle, out var ensuredAt);
            if (!known)
            {
                connection.EnableExtensions();
                // vec0 ships in the NuGet package — always available, no provisioning.
                connection.LoadVector();
            }

            // data_version moves when another connection commits; schema_version, user_version and
            // application_id move when anything, this handle included, changes what the version and
            // digest checks read. All four unchanged: only the every-open steps need to run.
            var bankState = await connection.QuerySingleAsync<BankState>(
                new CommandDefinition(BankStateSql, cancellationToken: cancellationToken));
            var unchanged = known && ensuredAt!.Value == bankState;
            var overlapResult = unchanged
                ? await MemorySchema.RunEveryOpenStepsAsync(connection, cancellationToken)
                : await MemorySchema.EnsureAsync(connection, cancellationToken);
            if (logger is not null)
            {
                if (overlapResult.Pruned.Count > 0)
                {
                    foreach (var watch in overlapResult.Pruned)
                    {
                        Log.WatchOverlapMigrationPruned(logger, watch.Path, watch.CoveredBy);
                    }

                    Log.WatchOverlapMigrationSummary(logger, overlapResult.Pruned.Count);
                }

                foreach (var warning in overlapResult.Warnings)
                {
                    Log.WatchOverlapMigrationSkippedProject(logger, warning.ProjectId, warning.Reason);
                }

                if (overlapResult.ScopelessEntriesRemoved > 0)
                {
                    Log.ScopelessEntriesRemoved(logger, overlapResult.ScopelessEntriesRemoved);
                }
            }

            if (!unchanged)
            {
                // Re-read: the full pass may itself have stamped the version or digest it brought up to date.
                bankState = await connection.QuerySingleAsync<BankState>(
                    new CommandDefinition(BankStateSql, cancellationToken: cancellationToken));
                InitializedHandles.AddOrUpdate(handle, new StrongBox<BankState>(bankState));
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private string BuildConnectionString(string? key)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = BankPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Wait up to 5s for the write lock instead of failing SQLITE_BUSY immediately: the
            // watch digest fans out concurrent writes (concurrency 4) through BEGIN IMMEDIATE, and a
            // lost-race failure drops the event for good (no re-enqueue), so contention must wait,
            // not throw.
            DefaultTimeout = 5
        };
        if (key is not null)
        {
            csb.Password = key;
        }

        return csb.ToString();
    }

    private async Task<SqliteConnection> OpenRekeyConnectionAsync(string? key, CancellationToken cancellationToken)
    {
        BankPaths.CreateDirectory(BankDirectoryFor(options));

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
        await connection.OpenAsync(cancellationToken);

        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=DELETE";
        await journal.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private static async Task OpenWithPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);

        await using var fk = connection.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys = ON";
        await fk.ExecuteNonQueryAsync(cancellationToken);

        await using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        await wal.ExecuteNonQueryAsync(cancellationToken);

        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        await busy.ExecuteNonQueryAsync(cancellationToken);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 901, Level = LogLevel.Information,
            Message = "watch overlap migration: removed {Path} (covered by {CoveredBy})")]
        public static partial void WatchOverlapMigrationPruned(ILogger logger, string path, string coveredBy);

        [LoggerMessage(EventId = 902, Level = LogLevel.Information,
            Message = "watch overlap migration: removed {Count} overlapping watch(es)")]
        public static partial void WatchOverlapMigrationSummary(ILogger logger, int count);

        [LoggerMessage(EventId = 903, Level = LogLevel.Warning,
            Message = "watch overlap migration: skipped project {ProjectId}, its watches were left untouched ({Reason})")]
        public static partial void WatchOverlapMigrationSkippedProject(ILogger logger, string projectId, string reason);

        [LoggerMessage(EventId = 904, Level = LogLevel.Information,
            Message = "schema migration: removed {Count} entries row(s) with neither a scope nor a workspace_id — unreachable by every tier")]
        public static partial void ScopelessEntriesRemoved(ILogger logger, long count);

        [LoggerMessage(EventId = 905, Level = LogLevel.Information,
            Message = "key check: rewrote the sidecar for {BankPath} — it no longer matched the key that just opened it")]
        public static partial void KeyCheckRewritten(ILogger logger, string bankPath);

        [LoggerMessage(EventId = 906, Level = LogLevel.Warning,
            Message = "key check: the sidecar for {BankPath} could not be written")]
        public static partial void KeyCheckUnavailable(ILogger logger, string bankPath, Exception exception);
    }
}
