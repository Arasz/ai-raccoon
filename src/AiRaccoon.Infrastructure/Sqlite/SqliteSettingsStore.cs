using AiRaccoon.Core.Memory;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The settings table, opened per call like every other bank access.</summary>
public sealed class SqliteSettingsStore(ISqliteConnectionFactory factory) : ISettingsStore
{
    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string?>(
                new CommandDefinition(MemorySql.SelectSetting, new { key }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.UpsertSetting, new { key, value }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SettingRow>(
                new CommandDefinition(MemorySql.SelectSettingsByPrefix, new { prefix },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
    }

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(MemorySql.DeleteSetting, new { key }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private sealed record SettingRow(string Key, string Value);
}
