using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>In-memory IMemoryStore for config-command tests: settings dict + configure recording.</summary>
internal sealed class FakeConfigStore : FakeMemoryStore
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    public (string Provider, string? Model, string? BaseUrl)? Configured { get; private set; }

    public override Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default)
    {
        Configured = (provider, model, baseUrl);
        Settings[EmbeddingSettingsKeys.Provider] = provider;
        if (model is null)
        {
            Settings.Remove(EmbeddingSettingsKeys.Model);
        }
        else
        {
            Settings[EmbeddingSettingsKeys.Model] = model;
        }

        if (baseUrl is null)
        {
            Settings.Remove(EmbeddingSettingsKeys.BaseUrl);
        }
        else
        {
            Settings[EmbeddingSettingsKeys.BaseUrl] = baseUrl;
        }

        Settings[EmbeddingSettingsKeys.Engine] = EmbeddingService.EngineFingerprint(provider, model, baseUrl);
        return Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled",
            EmbeddingService.EngineFingerprint(provider, model, baseUrl)));
    }

    /// <summary>ADR-0076: model set now reaches this via IModelMigrationStore, not ConfigureEmbeddingAsync — same recording shape.</summary>
    public override Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        ConfigureEmbeddingAsync(provider, model, baseUrl, cancellationToken);

    public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

    public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            Settings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

    public override Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Settings.Remove(key);
        return Task.CompletedTask;
    }
}
