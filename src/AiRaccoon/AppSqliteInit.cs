using System.Runtime.CompilerServices;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon;

internal static class AppSqliteInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        SqliteEncryptionInit.EnsureInitialized();
    }
}
