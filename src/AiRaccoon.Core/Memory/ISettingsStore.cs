namespace AiRaccoon.Core.Memory;

/// <summary>
///     The bank's settings table. Split out of <see cref="IMemoryStore" />, which still exposes the
///     same four members and delegates here (WP8, docs/plans/2026-08-14-code-quality-improvement-plan.md).
/// </summary>
public interface ISettingsStore
{
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);

    Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default);

    Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default);
}
