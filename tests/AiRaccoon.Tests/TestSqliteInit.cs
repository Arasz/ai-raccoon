using System.Runtime.CompilerServices;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Tests;

internal static class TestSqliteInit
{
    [ModuleInitializer]
    public static void Initialize() => SqliteEncryptionInit.EnsureInitialized();
}
