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

        var raw = typeof(raw);
        raw.GetMethod("SetProvider", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [Activator.CreateInstance(typeof(SQLite3Provider_sqlite3mc))]);
        raw.GetMethod("FreezeProvider", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [true]);
    }
}
