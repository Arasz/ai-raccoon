using AiRaccoon.Infrastructure.Options;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Opens the install's single memory bank (memory.db) with the shared PRAGMA policy, loads
///     vec0 (NuGet) and — when enabled — the cloudsync extension, and initializes our schema on
///     first open. There is no second meta database: every table lives in memory.db (FR-NM-1).
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly bool _loadCloudSync;
    private readonly Action<SqliteConnection> _loadExtensions;
    private readonly InfrastructureOptions _options;

    static SqliteConnectionFactory()
    {
        // Dapper maps columns to constructor parameters case-insensitively but not across
        // underscores; our schema uses snake_case (created_at, access_count, …).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqliteConnectionFactory(InfrastructureOptions options, bool loadCloudSync = false,
        Action<SqliteConnection>? loadExtensions = null)
    {
        _options = options;
        _loadCloudSync = loadCloudSync;
        _loadExtensions = loadExtensions ?? LoadNativeExtensions;
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
        // vec0 ships in the NuGet package — always available, no provisioning (plan §4).
        connection.LoadVector();
        _loadExtensions(connection);
        await MemorySchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private void LoadNativeExtensions(SqliteConnection connection)
    {
        if (!_loadCloudSync)
        {
            return;
        }

        // Sync stays extension-backed until P9 replaces it. A missing module disables sync
        // loudly at call time (no such function) rather than failing every bank open.
        var syncPath = ExtensionPaths.CloudSyncModulePath(_options.DataRoot, _options.Rid);
        if (File.Exists(syncPath))
        {
            connection.LoadExtension(syncPath);
        }
    }

    private static async Task OpenWithPragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
