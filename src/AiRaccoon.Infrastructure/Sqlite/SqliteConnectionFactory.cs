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
        await OpenBankWithKeyAsync(keyResolver.Resolve().Passphrase, cancellationToken).ConfigureAwait(false);

    /// <summary>Opens the bank with an explicit key (null = unencrypted): pragmas, vec0, schema.</summary>
    public async Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BankDirectoryFor(options));

        var connection = new SqliteConnection(BuildConnectionString(key));
        await OpenWithPragmasAsync(connection, cancellationToken).ConfigureAwait(false);

        connection.EnableExtensions();
        // vec0 ships in the NuGet package — always available, no provisioning.
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    ///     Rekeys the bank to a new key (raw x'…' or passphrase) on a DELETE-journal connection —
    ///     SQLCipher rekey is unsupported in WAL (plan §3.3) — then verifies by reopening. The
    ///     current-key pool is drained first; callers must not hold an open bank connection.
    /// </summary>
    public async Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(newKey);

        var currentKey = keyResolver.Resolve().Passphrase;
        SqliteConnection.ClearPool(new SqliteConnection(BuildConnectionString(currentKey)));

        await using (var connection = await OpenRekeyConnectionAsync(currentKey, cancellationToken).ConfigureAwait(false))
        {
            // quote() produces the same literal form Microsoft.Data.Sqlite uses for Password
            // (plan §5.2, measured F3) — it escapes both raw x'…' keys and passphrases.
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
