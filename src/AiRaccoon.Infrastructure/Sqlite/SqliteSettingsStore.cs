using AiRaccoon.Core.Memory;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The settings table, opened per call like every other bank access.</summary>
public sealed class SqliteSettingsStore(ISqliteConnectionFactory factory) : ISettingsStore
{
    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(MemorySql.SelectSetting, new { key }, cancellationToken: cancellationToken));
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.UpsertSetting, new { key, value }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var rows = await connection.QueryAsync<SettingRow>(
                new CommandDefinition(MemorySql.SelectSettingsByPrefix, new { prefix },
                    cancellationToken: cancellationToken));
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await factory.OpenBankAsync(cancellationToken);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.DeleteSetting, new { key }, cancellationToken: cancellationToken));
    }

    private sealed record SettingRow(string Key, string Value);
}
