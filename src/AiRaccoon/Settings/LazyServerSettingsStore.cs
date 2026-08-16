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
internal sealed class LazyServerSettingsStore : ISettingsStore, IModelMigrationStore
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

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        await AsMigrationStore(await InnerAsync(cancellationToken))
            .StartModelMigrationAsync(provider, model, baseUrl, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
        await AsMigrationStore(await InnerAsync(cancellationToken)).HasOpenModelMigrationAsync(cancellationToken);

    /// <summary>
    ///     Every store the acquire function can resolve to in production (<see cref="ServerSettingsStore" />)
    ///     implements both interfaces over the same connection; a cast failure here means a caller
    ///     built this with a settings-only fake and reached a model-migration method anyway.
    /// </summary>
    private static IModelMigrationStore AsMigrationStore(ISettingsStore store) =>
        store as IModelMigrationStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support model migration");

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
