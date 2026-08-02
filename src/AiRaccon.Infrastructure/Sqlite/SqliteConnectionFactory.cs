using AiRaccon.Infrastructure.Options;
using Microsoft.Data.Sqlite;

namespace AiRaccon.Infrastructure.Sqlite;

/// <summary>Opens the install's memory bank (one DB per install scope) with the shared PRAGMA policy and loads native extensions (spec §6.3).</summary>
public sealed class SqliteConnectionFactory
{
    private readonly InfrastructureOptions _options;
    private readonly bool _loadCloudSync;
    private readonly Action<SqliteConnection> _loadExtensions;

    public SqliteConnectionFactory(InfrastructureOptions options, bool loadCloudSync = false, Action<SqliteConnection>? loadExtensions = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loadCloudSync = loadCloudSync;
        _loadExtensions = loadExtensions ?? LoadNativeExtensions;
    }

    /// <summary>Directory holding the bank: the data root for user scope, &lt;dataRoot&gt;/.ai-raccon for project scope.</summary>
    public string BankDirectory => _options.Scope switch
    {
        InstallScope.User => _options.DataRoot,
        InstallScope.Project => Path.Combine(_options.DataRoot, ".ai-raccon"),
        _ => throw new ArgumentOutOfRangeException(nameof(_options.Scope), _options.Scope, "Unknown install scope."),
    };

    public string BankPath => Path.Combine(BankDirectory, "memory.db");

    public string MetaDatabasePath => Path.Combine(BankDirectory, "raccon_meta.db");

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
        connection.EnableExtensions(true);
        var paths = ExtensionPaths.For(_options.DataRoot, _options.Rid, _loadCloudSync);
        connection.LoadExtension(paths.Vector);
        connection.LoadExtension(paths.Memory);
        if (paths.CloudSync is not null)
        {
            connection.LoadExtension(paths.CloudSync);
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
