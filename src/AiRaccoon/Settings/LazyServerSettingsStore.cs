using AiRaccoon.Core.Memory;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Settings;

/// <summary>
///     Defers acquiring the settings backend (probe-and-start via <c>BackendLauncher</c>, ADR-0075
///     §5.1) until the first settings call, so a command bound to this store but that never calls
///     it — <c>serve</c> above all, whose own composition root ignores this one entirely — never
///     probes or auto-starts anything. The acquire result is cached for the lifetime of this
///     instance; a failed acquire is not cached and is retried on the next call.
/// </summary>
internal sealed class LazyServerSettingsStore : ISettingsStore
{
    private readonly Func<CancellationToken, Task<ISettingsStore>> _acquire;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISettingsStore? _inner;

    public LazyServerSettingsStore(Func<CancellationToken, Task<ISettingsStore>> acquire)
    {
        Guard.IsNotNull(acquire);
        _acquire = acquire;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        await (await InnerAsync(cancellationToken)).GetSettingAsync(key, cancellationToken);

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        await (await InnerAsync(cancellationToken)).SetSettingAsync(key, value, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        await (await InnerAsync(cancellationToken)).GetSettingsByPrefixAsync(prefix, cancellationToken);

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
        await (await InnerAsync(cancellationToken)).DeleteSettingAsync(key, cancellationToken);

    private async Task<ISettingsStore> InnerAsync(CancellationToken cancellationToken)
    {
        if (_inner is { } acquired)
        {
            return acquired;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _inner ??= await _acquire(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
