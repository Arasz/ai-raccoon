using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Settings;

/// <summary>
///     Defers acquiring the settings backend (probe-and-start via <c>BackendLauncher</c>, ADR-0075
///     §5.1) until the first settings call, so a command bound to this store but that never calls
///     it — <c>serve</c> above all, whose own composition root ignores this one entirely — never
///     probes or auto-starts anything. The acquire result is cached for the lifetime of this
///     instance; a failed acquire is not cached and is retried on the next call.
/// </summary>
internal sealed class LazyServerSettingsStore : ISettingsStore, IModelMigrationStore, ICodeEngineStore, IRepairStore,
    IPromotionQueuePruneStore, IMaintenanceStatsStore, INoiseSummaryStore, IWatchRegisteredStore
{
    private readonly Func<CancellationToken, Task<ISettingsStore>> _acquire;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISettingsStore? _inner;

    public LazyServerSettingsStore(Func<CancellationToken, Task<ISettingsStore>> acquire)
    {
        Guard.IsNotNull(acquire);
        _acquire = acquire;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => await (await InnerAsync(cancellationToken)).GetSettingAsync(key, cancellationToken);

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        await (await InnerAsync(cancellationToken)).SetSettingAsync(key, value, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        await (await InnerAsync(cancellationToken)).GetSettingsByPrefixAsync(prefix, cancellationToken);

    public async Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => await (await InnerAsync(cancellationToken)).DeleteSettingAsync(key, cancellationToken);

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        await AsMigrationStore(await InnerAsync(cancellationToken))
            .StartModelMigrationAsync(provider, model, baseUrl, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
        await AsMigrationStore(await InnerAsync(cancellationToken)).HasOpenModelMigrationAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory,
        CancellationToken cancellationToken = default) =>
        await AsCodeEngineStore(await InnerAsync(cancellationToken))
            .ActivateCodeEngineAsync(directory, cancellationToken);

    /// <inheritdoc />
    public async Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default) =>
        await AsRepairStore(await InnerAsync(cancellationToken)).ReportReingestAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default) =>
        await AsRepairStore(await InnerAsync(cancellationToken)).ReportChunkIndexAsync(cancellationToken);

    public async Task<ProjectIdCensusReport> ReportProjectIdsAsync(CancellationToken cancellationToken = default) =>
        await AsRepairStore(await InnerAsync(cancellationToken)).ReportProjectIdsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default) =>
        await AsRepairStore(await InnerAsync(cancellationToken)).RequestRepairAsync(kind, cancellationToken);

    /// <inheritdoc />
    public async Task<PromotionQueueOrphanReport> ReportPruneOrphansAsync(CancellationToken cancellationToken = default) =>
        await AsPruneStore(await InnerAsync(cancellationToken)).ReportPruneOrphansAsync(cancellationToken);

    /// <inheritdoc />
    public async Task RequestPruneOrphansAsync(CancellationToken cancellationToken = default) => await AsPruneStore(await InnerAsync(cancellationToken)).RequestPruneOrphansAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<BankStats> GetStatsAsync(CancellationToken cancellationToken = default) => await AsMaintenanceStatsStore(await InnerAsync(cancellationToken)).GetStatsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default) =>
        await AsNoiseSummaryStore(await InnerAsync(cancellationToken)).SummarizeAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default) =>
        await AsWatchRegisteredStore(await InnerAsync(cancellationToken)).ListWatchesAsync(cancellationToken);

    /// <summary>
    ///     Every store the acquire function can resolve to in production (<see cref="ServerSettingsStore" />)
    ///     implements both interfaces over the same connection; a cast failure here means a caller
    ///     built this with a settings-only fake and reached a model-migration method anyway.
    /// </summary>
    private static IModelMigrationStore AsMigrationStore(ISettingsStore store) =>
        store as IModelMigrationStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support model migration");

    /// <summary>Same reasoning as <see cref="AsMigrationStore" />, for the code-engine-activation capability.</summary>
    private static ICodeEngineStore AsCodeEngineStore(ISettingsStore store) =>
        store as ICodeEngineStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support code engine activation");

    /// <summary>Same reasoning as <see cref="AsMigrationStore" />, for the repair capability.</summary>
    private static IRepairStore AsRepairStore(ISettingsStore store) =>
        store as IRepairStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support repair requests");

    /// <summary>Same reasoning as <see cref="AsMigrationStore" />, for the promotion-queue-prune capability.</summary>
    private static IPromotionQueuePruneStore AsPruneStore(ISettingsStore store) =>
        store as IPromotionQueuePruneStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support promotion-queue prune requests");

    /// <summary>Same reasoning as <see cref="AsMigrationStore" />, for the maintenance-stats capability.</summary>
    private static IMaintenanceStatsStore AsMaintenanceStatsStore(ISettingsStore store) =>
        store as IMaintenanceStatsStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support maintenance stats");

    /// <summary>Same reasoning as <see cref="AsMigrationStore" />, for the noise-summary capability.</summary>
    private static INoiseSummaryStore AsNoiseSummaryStore(ISettingsStore store) =>
        store as INoiseSummaryStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support noise summary");

    /// <summary>Same reasoning as <see cref="AsMigrationStore" />, for the watch-registered capability.</summary>
    private static IWatchRegisteredStore AsWatchRegisteredStore(ISettingsStore store) =>
        store as IWatchRegisteredStore ?? throw new NotSupportedException(
            $"ai-raccoon: {store.GetType().Name} does not support watch-registered listing");

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
