using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Issue #466's nightly-class proof: the real default code engine, driven the way the product
///     drives it — <see cref="IEmbeddingService.CreateGenerator" /> over the downloaded manifest
///     directory — rather than the raw ONNX session. <see cref="CodeModelGraphWindowTests" /> runs
///     the graph directly and so never exercised the manifest's pooling mode at all, which is how a
///     model that pools inside its own graph reached the 1.32.0 checklist still throwing
///     ArgumentOutOfRangeException on every embed.
///     <para>
///         Needs the real 187 MB model on disk: point
///         <see cref="CodeModelGraphWindowTests.ModelDirEnvVar" /> at a downloaded
///         <c>faxenoff/code-daemon-embed-v1</c> directory, or these skip. The CI-runnable half of
///         the same defect is <c>OnnxEmbeddingGeneratorPoolingTests</c>.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class CodeEngineRealModelEmbedTests
{
    private const string AddFunction = "def add(a, b):\n    \"\"\"Return the sum of a and b.\"\"\"\n    return a + b\n";

    private const string ReadFileFunction =
        "def read_config(path):\n    \"\"\"Load the JSON configuration file at path.\"\"\"\n"
        + "    with open(path) as handle:\n        return json.load(handle)\n";

    [Fact]
    public async Task TheCodeEngine_EmbedsACodeChunk_AtTheManifestsDimensions()
    {
        var descriptor = LoadDescriptorOrSkip(out var modelDirectory);
        var service = TestData.CreateEmbeddingService();
        var settings = new EmbeddingSettings("local", modelDirectory, null, null);
        using var generator = service.CreateGenerator(settings);

        var result = await generator.GenerateAsync([AddFunction, ReadFileFunction],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2, "one vector per chunk, never per token");
        foreach (var embedding in result)
        {
            var vector = embedding.Vector.ToArray();
            vector.Length.ShouldBe(descriptor.Dimensions);
            vector.ShouldAllBe(v => float.IsFinite(v));
            Norm(vector).ShouldBe(1.0, 1e-5, "the manifest declares normalization=l2");
        }
    }

    /// <summary>
    ///     A wrongly-pooled vector is still the right length and still normalized, so shape checks
    ///     alone cannot tell a working engine from a broken one. Meaning can: a natural-language
    ///     query must sit closer to the function it describes than to an unrelated one.
    /// </summary>
    [Fact]
    public async Task TheCodeEngine_PutsAQueryNearerTheFunctionItDescribes()
    {
        LoadDescriptorOrSkip(out var modelDirectory);
        var service = TestData.CreateEmbeddingService();
        var settings = new EmbeddingSettings("local", modelDirectory, null, null);
        using var generator = service.CreateGenerator(settings);

        var result = await generator.GenerateAsync(["load configuration from a json file", AddFunction, ReadFileFunction],
            cancellationToken: TestContext.Current.CancellationToken);

        var query = result[0].Vector.ToArray();
        Cosine(query, result[2].Vector.ToArray()).ShouldBeGreaterThan(Cosine(query, result[1].Vector.ToArray()),
            "the config-reading function must rank above integer addition for a config-reading query");
    }

    /// <summary>A chunk sized to the whole code budget still embeds — the largest input the chunker can emit.</summary>
    [Fact]
    public async Task TheCodeEngine_EmbedsAChunkFillingTheWholeCodeBudget()
    {
        var descriptor = LoadDescriptorOrSkip(out var modelDirectory);
        var service = TestData.CreateEmbeddingService();
        var settings = new EmbeddingSettings("local", modelDirectory, null, null);
        var tokenizer = service.ResolveTokenizer(settings)!;
        using var generator = service.CreateGenerator(settings);
        var budget = service.ResolveChunkBudgetFor(settings);
        budget.ShouldBe(CodeChunker.DefaultBudget, "the engine and the chunker must agree on the budget");

        var chunk = ChunkOfTokens(tokenizer, budget);

        var result = await generator.GenerateAsync([chunk], cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveSingleItem().Vector.Length.ShouldBe(descriptor.Dimensions);
    }

    /// <summary>The adaptation is deliberate but reported: event 417 names the model whose manifest to correct.</summary>
    [Fact]
    public async Task TheCodeEngine_WarnsThatTheManifestsPoolingModeCannotApply()
    {
        var descriptor = LoadDescriptorOrSkip(out var modelDirectory);
        var logger = new FakeLogger<OnnxEmbeddingGenerator>();
        var tokenizerFactory = new EmbeddingTokenizerFactory();
        using var generator = new OnnxEmbeddingGenerator(Path.Combine(modelDirectory, descriptor.OnnxModelFile),
            tokenizerFactory.Create(descriptor, modelDirectory), descriptor, logger);

        await generator.GenerateAsync([AddFunction], cancellationToken: TestContext.Current.CancellationToken);

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(417);
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain(descriptor.Model);
    }

    private static EngineDescriptor LoadDescriptorOrSkip(out string modelDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(CodeModelGraphWindowTests.ModelDirEnvVar);
        if (string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured))
        {
            Assert.Skip($"set {CodeModelGraphWindowTests.ModelDirEnvVar} to a downloaded "
                        + $"faxenoff/code-daemon-embed-v1 directory ('{CodeEngineSetup.DefaultModelCommand}' puts one "
                        + "at <data-root>/models/); the weights are too large to ship with the suite");
        }

        modelDirectory = Path.GetFullPath(configured);
        return new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(modelDirectory);
    }

    /// <summary>Real code text grown until the engine's own tokenizer counts exactly <paramref name="tokens" /> content tokens.</summary>
    private static string ChunkOfTokens(IEmbeddingTokenizer tokenizer, int tokens)
    {
        var lines = new List<string>();
        while (tokenizer.CountTokens(string.Join("\n", lines)) < tokens)
        {
            lines.Add($"    total_{lines.Count} = compute_bucket_weight(index_{lines.Count}, offset)");
        }

        var text = string.Join("\n", lines);
        while (tokenizer.CountTokens(text) > tokens)
        {
            text = text[..^1];
        }

        tokenizer.CountTokens(text).ShouldBe(tokens);
        return text;
    }

    private static double Norm(IReadOnlyList<float> vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            sum += (double)value * value;
        }

        return Math.Sqrt(sum);
    }

    private static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot / (Norm(a) * Norm(b));
    }
}
