using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>In-memory IMemoryStore for config-command tests: settings dict + configure recording.</summary>
internal sealed class FakeConfigStore : FakeMemoryStore, ICodeEngineStore
{
    private static EmbeddingService Fingerprint() =>
        new(NullLogger<EmbeddingService>.Instance, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System);

    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    public (string Provider, string? Model, string? BaseUrl)? Configured { get; private set; }

    /// <summary>ADR-0076: model set reaches this via IModelMigrationStore — the fake just records it.</summary>
    public override Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
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

        Settings[EmbeddingSettingsKeys.Engine] = Fingerprint().EngineFingerprint(provider, model, baseUrl);
        return Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled",
            Fingerprint().EngineFingerprint(provider, model, baseUrl)));
    }

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

    public (string Directory, string Engine)? CodeActivated { get; private set; }

    /// <summary>§3.3 D-E9: same recording shape as StartModelMigrationAsync, over the code engine's own settings rows.</summary>
    public Task<EmbeddingConfig> ActivateCodeEngineAsync(string directory, CancellationToken cancellationToken = default)
    {
        var fingerprint = Fingerprint().EngineFingerprint("local", directory, null);
        CodeActivated = (directory, fingerprint);
        Settings[EmbeddingSettingsKeys.CodeModel] = directory;
        Settings[EmbeddingSettingsKeys.CodeEngine] = fingerprint;
        return Task.FromResult(new EmbeddingConfig("local", directory, fingerprint));
    }
}
