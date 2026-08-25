using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     #522: `settings model threads &lt;n&gt;` round-trips through the bank, but nothing observable
///     confirmed the resolved ORT intra-op thread count actually took effect. A session build now
///     logs the resolved count and its source (an explicit setting vs the halved-core default).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EmbeddingSessionThreadLoggingTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("embedding-session-thread-logging");

    public void Dispose() => TestData.DeleteTempRoot(_root);

    private static EmbeddingService CreateService(FakeLogger<EmbeddingService> logger, InMemorySettings settings) =>
        new(logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System, settings);

    private string CopyBundledModel()
    {
        var custom = Path.Combine(_root, "custom.onnx");
        File.Copy(BundledModel.ResolveModelPath(), custom);
        return custom;
    }

    [RetryFact]
    public void CreateGenerator_Local_ExplicitThreadsSetting_LogsResolvedCountAndSource()
    {
        var logger = new FakeLogger<EmbeddingService>();
        var settings = new InMemorySettings();
        settings.Values[EmbeddingSettingsKeys.Threads] = "3";
        var service = CreateService(logger, settings);

        using var generator = service.CreateGenerator(new EmbeddingSettings("local", CopyBundledModel(), null, null));

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(428);
        record.Level.ShouldBe(LogLevel.Information);
        record.StructuredState.ShouldNotBeNull();
        record.StructuredState!.Single(kv => kv.Key == "Threads").Value.ShouldBe("3");
        record.StructuredState!.Single(kv => kv.Key == "ThreadsDisplay").Value.ShouldBe("3");
        record.StructuredState!.Single(kv => kv.Key == "Source").Value.ShouldBe("setting");
    }

    [RetryFact]
    public void CreateGenerator_Local_UnsetThreadsSetting_LogsHalvedCoreDefaultAsSource()
    {
        var logger = new FakeLogger<EmbeddingService>();
        var settings = new InMemorySettings();
        var service = CreateService(logger, settings);
        var expectedThreads = Math.Max(1, Environment.ProcessorCount / 2);

        using var generator = service.CreateGenerator(new EmbeddingSettings("local", CopyBundledModel(), null, null));

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(428);
        record.StructuredState!.Single(kv => kv.Key == "Threads").Value.ShouldBe(expectedThreads.ToString());
        record.StructuredState!.Single(kv => kv.Key == "ThreadsDisplay").Value.ShouldBe(expectedThreads.ToString());
        record.StructuredState!.Single(kv => kv.Key == "Source").Value.ShouldBe("halved-core default");
    }

    /// <summary>
    ///     #522 review: 0 is a real setting meaning "ORT's own default", not zero threads — a bare
    ///     "0" misreads as broken. WP4/#524 follow-up: the structured `Threads` value stays the raw
    ///     `int` a sink can aggregate on; the human phrase lives in its own `ThreadsDisplay` field.
    /// </summary>
    [RetryFact]
    public void CreateGenerator_Local_ExplicitZeroThreadsSetting_LogsOrtDefaultDisplay()
    {
        var logger = new FakeLogger<EmbeddingService>();
        var settings = new InMemorySettings();
        settings.Values[EmbeddingSettingsKeys.Threads] = "0";
        var service = CreateService(logger, settings);

        using var generator = service.CreateGenerator(new EmbeddingSettings("local", CopyBundledModel(), null, null));

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(428);
        record.StructuredState!.Single(kv => kv.Key == "Threads").Value.ShouldBe("0");
        record.StructuredState!.Single(kv => kv.Key == "ThreadsDisplay").Value.ShouldBe("ORT default");
        record.StructuredState!.Single(kv => kv.Key == "Source").Value.ShouldBe("setting");
    }
}
