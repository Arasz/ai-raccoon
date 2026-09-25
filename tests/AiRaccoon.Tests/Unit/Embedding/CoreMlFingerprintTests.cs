using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>ADR-0118: Neural Engine vectors sit inside the parity bar, so switching to or from <c>device coreml</c> never re-embeds a bank.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CoreMlFingerprintTests
{
    [Fact]
    public void BundledFingerprint_IsTheSameUnderDeviceCoreMlAndAuto()
    {
        var coreMl = Service("coreml").EngineFingerprint("local", null, null);
        var auto = Service("auto").EngineFingerprint("local", null, null);

        coreMl.ShouldBe(auto);
    }

    private static EmbeddingService Service(string device)
    {
        var store = new InMemorySettings();
        store.Values[EmbeddingSettingsKeys.Device] = device;
        return new EmbeddingService(NullLogger<EmbeddingService>.Instance, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System,
            new InfrastructureOptions { DataRoot = Path.GetTempPath(), Scope = InstallScope.User }, store);
    }
}
