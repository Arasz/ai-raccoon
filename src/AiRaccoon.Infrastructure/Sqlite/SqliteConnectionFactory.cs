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
public sealed class SqliteConnectionFactory
{
    private readonly InfrastructureOptions _options;

    static SqliteConnectionFactory()
    {
        // Dapper maps columns to constructor parameters case-insensitively but not across
        // underscores; our schema uses snake_case (created_at, access_count, …).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteConnectionFactory(InfrastructureOptions options)
    {
        _options = options;
    }

    /// <summary>Directory holding the bank: the data root for user scope, &lt;dataRoot&gt;/.ai-raccoon for project scope.</summary>
    private string BankDirectory =>
        _options.Scope switch
        {
            InstallScope.User => _options.DataRoot,
            InstallScope.Project => Path.Combine(_options.DataRoot, ".ai-raccoon"),
            _ => throw new ArgumentOutOfRangeException(nameof(_options.Scope), _options.Scope,
                "Unknown install scope.")
        };

    public string BankPath => Path.Combine(BankDirectory, "memory.db");

    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BankDirectory);

        var connection = new SqliteConnection($"Data Source={BankPath}");
        await OpenWithPragmasAsync(connection, cancellationToken).ConfigureAwait(false);

        connection.EnableExtensions();
        // vec0 ships in the NuGet package — always available, no provisioning.
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        await SeedGlobalAccessModeAsync(connection, cancellationToken).ConfigureAwait(false);

        return connection;
    }

    // FR-NM-2: the global access mode is seeded once from the AIRACCOON_ACCESS_MODE env value
    // (ro|rw|full); an operator-set settings row is never overwritten by the seed.
    private static async Task SeedGlobalAccessModeAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var env = Environment.GetEnvironmentVariable("AIRACCOON_ACCESS_MODE");
        if (AccessModePolicy.Parse(env) is not { } mode)
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
