using AiRaccon.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AiRaccon.Infrastructure.Sqlite;

/// <summary>Maps dbmem_content rows (hash, path, context, value, created_at) to MemoryEntry.</summary>
internal static class MemoryRowMapper
{
    public static MemoryEntry ToEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4));
}
