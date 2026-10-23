using System.Runtime.InteropServices;
using System.Text;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0118: under <c>device coreml</c> the bundled engine switches to the Neural Engine once its
///     four bucket sessions compile, and its vectors match the CPU session's at every row length,
///     including rows past the largest bucket. macOS on Apple Silicon only.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(NeuralEngineCollection.Name)]
public sealed class BundledEngineCoreMlSessionTests(ITestOutputHelper output) : IDisposable
{
    private const int NeuralEngineServingEventId = 437;
    private static readonly TimeSpan SettleWithin = TimeSpan.FromMinutes(6);
    private readonly string _dataRoot = TestData.CreateTempRoot("coreml-bundled");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task DeviceCoreMl_SwitchesToTheNeuralEngine_WithTheCpuSessionsVectors()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.Skip("the CoreML Neural Engine path runs on macOS on Apple Silicon only");
        }

        var ct = TestContext.Current.CancellationToken;
        var settings = new EmbeddingSettings("local", null, null, null);
        var cold = await SettledGeneratorAsync(settings, ct);
        using var cpu = CpuGenerator();
        var tokenizer = new EmbeddingTokenizerFactory().Create(Descriptor(), BundledModel.ResolveDirectory());

        cold.Generator.ExecutionProvider.ShouldBe("CoreML");
        cold.Generator.Switch.History.ShouldNotContain(t => t.To == NeuralEngineState.Refused);
        foreach (var tokens in (int[])[10, 300, 700, 1000, 1500])
        {
            var text = TextOf(tokenizer, tokens);
            var onNeuralEngine = await cold.Generator.GenerateAsync([text], cancellationToken: ct);
            var onCpu = await cpu.GenerateAsync([text], cancellationToken: ct);
            var cosine = TestData.Cosine(onNeuralEngine[0].Vector, onCpu[0].Vector);
            output.WriteLine($"{tokenizer.EncodeToIds(text, true).Count} tokens: cosine {cosine:F6}");
            cosine.ShouldBeGreaterThanOrEqualTo(0.999, $"{tokens}-token row");
        }

        var set = Directory.GetDirectories(Path.Combine(_dataRoot, "coreml-cache")).Single();
        var ortSet = Directory.GetDirectories(set).Single();
        File.Exists(Path.Combine(ortSet, CoreMlCache.CompleteMarkerName)).ShouldBeTrue();
        Directory.GetDirectories(Path.Combine(ortSet, "CPUAndNeuralEngine")).Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .ShouldBe(["bucket-1024", "bucket-256", "bucket-512", "bucket-768"]);
        var sizeMiB = DirectorySize(Path.Combine(_dataRoot, "coreml-cache")) / (1024.0 * 1024.0);
        cold.Generator.Dispose();

        var warm = await SettledGeneratorAsync(settings, ct);
        warm.Generator.ExecutionProvider.ShouldBe("CoreML");
        warm.Generator.Dispose();

        output.WriteLine($"cold switch: {cold.Message}");
        output.WriteLine($"warm switch: {warm.Message}");
        output.WriteLine($"cache size: {sizeMiB:F0} MiB");
    }

    [RetryFact]
    public void TwoInstallPaths_ShareOneCompiledModel_NamedByTheGraphsCacheKey()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Assert.Skip("the CoreML Neural Engine path runs on macOS on Apple Silicon only");
        }

        var bucket = CoreMlGraph.Buckets[0];
        var cacheDirectory = Path.Combine(_dataRoot, "coreml-cache", "bucket");
        foreach (var version in (string[])["2.2.0", "2.3.0"])
        {
            var install = InstallCopy(version);
            var factory = new CoreMlSessionFactory(Path.Combine(install, CoreMlGraph.FileName), Path.Combine(install, "model_fp16.onnx"),
                new EmbeddingTokenizerFactory().Create(Descriptor(), BundledModel.ResolveDirectory()), Descriptor(), 0,
                NullLogger.Instance);
            using var session = factory.CreateBucket(bucket, cacheDirectory);
            session.Run(new long[bucket], new long[bucket]).ShouldNotBeEmpty();
        }

        Directory.GetDirectories(cacheDirectory).Select(Path.GetFileName).ShouldBe([CoreMlGraph.CacheKey],
            "a compiled model per install path means every tool update compiles and keeps another copy");
    }

    /// <summary>The ANE graph and the weights it reads, copied the way a tool install lays them out under a versioned path.</summary>
    private string InstallCopy(string version)
    {
        var source = BundledModel.ResolveDirectory();
        var install = Path.Combine(_dataRoot, "store", version);
        Directory.CreateDirectory(install);
        foreach (var file in (string[])[CoreMlGraph.FileName, CoreMlGraph.WeightsFileName])
        {
            File.Copy(Path.Combine(source, file), Path.Combine(install, file));
        }

        return install;
    }

    private async Task<(NeuralEngineEmbeddingGenerator Generator, string Message)> SettledGeneratorAsync(
        EmbeddingSettings settings, CancellationToken ct)
    {
        var logger = new FakeLogger<EmbeddingService>();
        var store = new InMemorySettings();
        store.Values[EmbeddingSettingsKeys.Device] = "coreml";
        var service = new EmbeddingService(logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            NoOpMeasurementRecorder.Instance, TimeProvider.System,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, store);

        var generator = service.CreateGenerator(settings).ShouldBeOfType<NeuralEngineEmbeddingGenerator>();
        (await generator.WaitUntilSettledAsync(ct).WaitAsync(SettleWithin, ct)).ShouldBe(NeuralEngineState.NeuralEngineServing);
        var message = logger.Collector.GetSnapshot().Single(r => r.Id.Id == NeuralEngineServingEventId).Message;
        return (generator, message);
    }

    private static OnnxEmbeddingGenerator CpuGenerator()
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = Descriptor();
        return new OnnxEmbeddingGenerator(Path.Combine(directory, descriptor.OnnxModelFile),
            new EmbeddingTokenizerFactory().Create(descriptor, directory), descriptor, NullLogger.Instance);
    }

    private static EngineDescriptor Descriptor() =>
        new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(BundledModel.ResolveDirectory());

    private static string TextOf(IEmbeddingTokenizer tokenizer, int tokens)
    {
        const string sentence = "Memory rows are chunked, embedded and searched by meaning across every project.";
        var text = new StringBuilder(sentence);
        while (tokenizer.EncodeToIds(text.ToString(), true).Count < tokens)
        {
            text.Append(' ').Append(sentence);
        }

        return text.ToString();
    }

    private static long DirectorySize(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}
