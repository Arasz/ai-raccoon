using SQLitePCL;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Sets up the sqlite3mc provider (SQLite3MC.PCLRaw bundle) before any SQLite operations.
///     Must be called from an entry-point ModuleInitializer — library-level
///     [ModuleInitializer] does not fire in referenced assemblies.
/// </summary>
public static class SqliteEncryptionInit
{
    private const int MemStatusConfigOption = 9;
    private static bool _initialized;

    /// <summary>Idempotent: sets and freezes the sqlite3mc provider.</summary>
    /// <remarks>
    /// SQLITE_CONFIG_MEMSTATUS off: with it on, every SQLite malloc/free takes one process-wide
    /// mutex, so concurrent connections serialize on allocation. Must precede the library's
    /// initialization, which the first connection triggers.
    /// </remarks>
    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        raw.SetProvider(new SQLite3Provider_sqlite3mc());
        raw.FreezeProvider();
        raw.sqlite3_config(MemStatusConfigOption, 0);
    }
}
