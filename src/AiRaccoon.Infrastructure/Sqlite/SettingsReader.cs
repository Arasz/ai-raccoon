using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The one raw settings read, run on the caller's own connection so it sees that connection's open transaction.</summary>
public static class SettingsReader
{
    extension(SqliteConnection connection)
    {
        /// <summary>The stored value for <paramref name="key" />: null when absent, otherwise the text exactly as written (empty or malformed included).</summary>
        public async Task<string?> ReadSettingAsync(string key, CancellationToken cancellationToken) =>
            await connection.QuerySingleOrDefaultAsync<string?>(
                Sql.Def(MemorySql.SelectSetting, new { key }, cancellationToken));
    }
}
