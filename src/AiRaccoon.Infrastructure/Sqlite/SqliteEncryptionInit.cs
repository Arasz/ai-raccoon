using System.Reflection;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Sets up the e_sqlite3mc provider before any SQLite operations.
///     Must be called from an entry-point ModuleInitializer — library-level
///     [ModuleInitializer] does not fire in referenced assemblies.
///     TODO: remove when Microsoft.Data.Sqlite 11.0+ bundles e_sqlite3mc natively.
/// </summary>
public static class SqliteEncryptionInit
{
    private static bool _initialized;

    /// <summary>Idempotent: sets and freezes the e_sqlite3mc provider.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        var raw = typeof(SQLitePCL.raw);
        raw.GetMethod("SetProvider", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [Activator.CreateInstance(typeof(SQLitePCL.SQLite3Provider_e_sqlite3mc))]);
        raw.GetMethod("FreezeProvider", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, [true]);
    }
}
