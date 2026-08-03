using AiRaccoon.Infrastructure.Options;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Opens the install's memory bank (one DB per install scope) with the shared PRAGMA policy and loads native extensions (spec §6.3).</summary>
public sealed class SqliteConnectionFactory
{
    private readonly bool _loadCloudSync;
    private readonly Action<SqliteConnection> _loadExtensions;
    private readonly InfrastructureOptions _options;

    static SqliteConnectionFactory()
    {
        // Dapper maps columns to constructor parameters case-insensitively but not across
        // underscores; the sqlite-memory schema uses snake_case (created_at, access_count, …).
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
                "Unknown install scope."),
        };

    public string BankPath => Path.Combine(BankDirectory, "memory.db");

    public string MetaDatabasePath => Path.Combine(BankDirectory, "raccoon_meta.db");

    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BankDirectory);

        var connection = new SqliteConnection($"Data Source={BankPath}");
        await OpenWithPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        _loadExtensions(connection);
        return connection;
    }

    public async Task<SqliteConnection> OpenMetaAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BankDirectory);

        var connection = new SqliteConnection($"Data Source={MetaDatabasePath}");
        await OpenWithPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void LoadNativeExtensions(SqliteConnection connection)
    {
        connection.EnableExtensions();
        var paths = ExtensionPaths.For(_options.DataRoot, _options.Rid, _loadCloudSync);
        connection.LoadExtension(paths.Vector);
        connection.LoadExtension(paths.Memory);
        if (paths.CloudSync is not null)
        {
            connection.LoadExtension(paths.CloudSync);
        }

        // Writes must work before any embedding model is configured: defer embeddings by
        // default (FR-MEM-1.12); memory_configure turns deferral off once a model is set.
        // Only apply the deferral default when no provider is persisted yet — re-asserting it
        // on every open would clobber a configured model's deferral-off (M1).
        using var provider = connection.CreateCommand();
        provider.CommandText = "SELECT memory_get_option('provider')";
        var configuredProvider = provider.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(configuredProvider))
        {
            using var defer = connection.CreateCommand();
            defer.CommandText = MemorySql.SetDeferEmbeddings;
            defer.Parameters.AddWithValue("@value", 1);
            defer.ExecuteScalar();
        }

        // Path-scoped hashes: identical content may live in several contexts (a project row and
        // its shared promotion, or the same fact in two projects). add_text still dedups within
        // a context, which keeps FR-MEM-1.8; add_content with a distinct path creates the
        // second row (B1).
        using var preserve = connection.CreateCommand();
        preserve.CommandText = MemorySql.SetPreserveDuplicatePaths;
        preserve.Parameters.AddWithValue("@value", 1);
        preserve.ExecuteScalar();
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
