using System.Reflection;
using System.Runtime.CompilerServices;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Tests;

internal static class TestSqliteInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        SqliteEncryptionInit.EnsureInitialized();

        // Verify it worked
        try
        {
            var libName = typeof(SQLitePCL.raw).GetMethod("GetNativeLibraryName",
                BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null);
            System.Diagnostics.Debug.WriteLine($"[TestSqliteInit] Native library: {libName}");
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("[TestSqliteInit] Provider not yet set");
        }
    }
}
