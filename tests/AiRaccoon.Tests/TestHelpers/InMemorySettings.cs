using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A settings store backed by a dictionary, for services that read settings but are not the
///     subject of the test — <see cref="AiRaccoon.Core.Memory.QueryGuard.QueryGuardService" />
///     above all. Unset keys return null, which is what an unconfigured bank returns, so every
///     `Parse*` helper falls to its documented default.
/// </summary>
public sealed class InMemorySettings : ISettingsStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.GetValueOrDefault(key));

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            Values.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }
}
