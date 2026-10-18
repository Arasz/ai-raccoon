using System.Reflection;
using SQLitePCL;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Sets up the sqlite3mc provider (SQLite3MC.PCLRaw bundle) before any SQLite operations.
///     Must be called from an entry-point ModuleInitializer — library-level
///     [ModuleInitializer] does not fire in referenced assemblies.
/// </summary>
public static class SqliteEncryptionInit
{
    private static bool _initialized;

    /// <summary>Idempotent: sets and freezes the sqlite3mc provider.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var rawType = typeof(raw);
        rawType.GetMethod("SetProvider", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [Activator.CreateInstance(typeof(SQLite3Provider_sqlite3mc))]);
        rawType.GetMethod("FreezeProvider", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [true]);

        // SQLITE_CONFIG_MEMSTATUS off: with it on, every SQLite malloc/free takes one process-wide
        // mutex, so concurrent connections serialise on allocation. Must precede the library's
        // initialisation, which the first connection triggers.
        raw.sqlite3_config(MemStatusConfigOption, 0);
    }

    private const int MemStatusConfigOption = 9;
}
