using AiRaccoon.Core.Access;
using AiRaccoon.Infrastructure.Options;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Opens the install's single memory bank (memory.db) with the shared PRAGMA policy, loads
///     vec0 (NuGet), and initializes our schema on first open. There is no second meta database:
///     every table lives in memory.db (FR-NM-1).
/// </summary>
public sealed class SqliteConnectionFactory(InfrastructureOptions options, IEncryptionKeyProvider keyProvider)
{
    static SqliteConnectionFactory()
    {
        // Dapper maps columns to constructor parameters case-insensitively but not across
        // underscores; our schema uses snake_case (created_at, access_count, …).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <summary>Directory holding the bank: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon for project scope.</summary>
    private string BankDirectory =>
        options.Scope switch
        {
            InstallScope.User => options.DataRoot,
            InstallScope.Project => Path.Combine(options.DataRoot, ".ai-raccoon"),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Scope), options.Scope,
                "Unknown install scope.")
        };

    public string BankPath => Path.Combine(BankDirectory, "memory.db");

    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BankDirectory);

        var passphrase = keyProvider.GetPassphrase();
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = BankPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        if (passphrase is not null)
        {
            csb.Password = passphrase;
        }

        var connection = new SqliteConnection(csb.ToString());
        await OpenWithPragmasAsync(connection, cancellationToken).ConfigureAwait(false);

        connection.EnableExtensions();
        // vec0 ships in the NuGet package — always available, no provisioning.
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        await SeedGlobalAccessModeAsync(connection, cancellationToken).ConfigureAwait(false);

        return connection;
    }

    // FR-NM-2: the global access mode is seeded once from the merged options.AccessMode
    // value (ro|rw|full, CLI > env); an operator-set settings row is never overwritten by
    // the seed.
    private async Task SeedGlobalAccessModeAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (AccessModePolicy.Parse(options.AccessMode) is not { } mode)
        {
            return;
        }

        await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO NOTHING",
                    new { key = AccessModePolicy.GlobalSettingKey, value = AccessModePolicy.Serialize(mode) },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
