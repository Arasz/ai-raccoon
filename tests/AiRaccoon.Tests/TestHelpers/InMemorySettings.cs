using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A settings store backed by a dictionary, for services that read settings but are not the
///     subject of the test — <see cref="AiRaccoon.Core.Memory.QueryGuard.QueryGuardService" />
///     above all. Unset keys return null, which is what an unconfigured bank returns, so every
///     `Parse*` helper falls to its documented default.
/// </summary>
public sealed class InMemorySettings : ISettingsStore, IModelMigrationStore, IRepairStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Every argument this was last called with, for a test to assert routing without a real bank (ADR-0076).</summary>
    public (string Provider, string? Model, string? BaseUrl)? LastModelMigrationRequest { get; private set; }

    /// <summary>Every kind this was last asked to request, for a test to assert routing without a real bank (ADR-0075 amendment).</summary>
    public RepairKind? LastRepairRequest { get; private set; }

    public ReingestRepairReport ReingestReport { get; set; } = new(0, 0, 0);

    public ChunkIndexRepairReport ChunkIndexReport { get; set; } = new(0, 0, 0);

    public Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ReingestReport);

    public Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ChunkIndexReport);

    public Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default)
    {
        LastRepairRequest = kind;
        return Task.CompletedTask;
    }

    public Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default)
    {
        LastModelMigrationRequest = (provider, model, baseUrl);
        return Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", $"{provider}:{model}"));
    }

    public Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

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
