using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Embedding;

/// <summary>
///     EmbeddingService behavior: the local engine runs the bundled ONNX model in-process
///     (dim 384, mean-pool + L2-normalize), a custom model path overrides it, and an
///     OpenAI-compatible provider routes through its base URL.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EmbeddingServiceTests : IAsyncLifetime
{
    private FakeEmbeddingEndpoint? _openAi;

    public async ValueTask InitializeAsync()
    {
        await BundledModel.EnsureAsync(TestContext.Current.CancellationToken);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_openAi is not null)
        {
            await _openAi.DisposeAsync();
        }
    }

    [Fact]
    public async Task Local_EmbedsTextInProcess_With384Dims_UnitNorm()
    {
        using var generator = new EmbeddingService().CreateGenerator(
            new EmbeddingSettings("local", Model: null, BaseUrl: null, ApiKey: null));

        var result = await generator.GenerateAsync(["the quick brown fox jumps over the lazy dog"],
            cancellationToken: TestContext.Current.CancellationToken);

        var vector = result[0].Vector;
        vector.Length.ShouldBe(384);
        var norm = Math.Sqrt(vector.ToArray().Sum(v => (double)v * v));
        norm.ShouldBe(1.0, 1e-3);
    }

    [Fact]
    public async Task Local_SimilarSentences_AreCloserThanUnrelatedOnes()
    {
        using var generator = new EmbeddingService().CreateGenerator(
            new EmbeddingSettings("local", Model: null, BaseUrl: null, ApiKey: null));

        var result = await generator.GenerateAsync(
            [
                "SQLite stores project knowledge for agents", "SQLite stores project knowledge for agents",
                "the weather in Paris is sunny today"
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        var same = Cosine(result[0].Vector, result[1].Vector);
        var unrelated = Cosine(result[0].Vector, result[2].Vector);
        same.ShouldBeGreaterThan(unrelated);
        same.ShouldBeGreaterThan(0.99);
    }

    [Fact]
    public async Task Local_CustomModelPath_OverridesTheBundledModel()
    {
        // A custom path — here a copy of the bundled model in a temp location — must be used
        // instead of the bundled copy (FR-NM-3 scenario 2).
        var custom = Path.Combine(Path.GetTempPath(), "ai-raccoon-custom-model",
            Guid.NewGuid().ToString("N"), BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.Copy(BundledModel.ResolveModelPath(), custom);

        try
        {
            using var generator = new EmbeddingService().CreateGenerator(
                new EmbeddingSettings("local", Model: custom, BaseUrl: null, ApiKey: null));

            var result = await generator.GenerateAsync(["custom path embedding"],
                cancellationToken: TestContext.Current.CancellationToken);

            result[0].Vector.Length.ShouldBe(384);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(custom)!, true);
        }
    }

    [Fact]
    public async Task OpenAi_RoutesThroughBaseUrl_WithModelAndApiKey()
    {
        using var generator = new EmbeddingService().CreateGenerator(
            new EmbeddingSettings("openai", Model: "nomic-embed-text", BaseUrl: _openAi!.BaseUrl,
                ApiKey: "test-key-123"));

        var result = await generator.GenerateAsync(["routed through the fake endpoint"],
            cancellationToken: TestContext.Current.CancellationToken);

        var request = _openAi.Requests.ShouldHaveSingleItem();
        request.Model.ShouldBe("nomic-embed-text");
        request.Inputs.ShouldBe(["routed through the fake endpoint"]);
        request.Authorization.ShouldBe("Bearer test-key-123");
        result[0].Vector.Length.ShouldBe(384);
        result[0].Vector.Span[0].ShouldBe(FakeEmbeddingEndpoint.VectorFor("routed through the fake endpoint")[0]);
    }

    [Fact]
    public void OpenAi_WithoutApiKey_Throws()
    {
        var service = new EmbeddingService();

        Should.Throw<InvalidOperationException>(() => service.CreateGenerator(
            new EmbeddingSettings("openai", Model: "nomic-embed-text", BaseUrl: _openAi!.BaseUrl, ApiKey: null)));
    }

    [Fact]
    public void EngineFingerprint_LocalWithoutModel_IsBundled()
    {
        EmbeddingService.EngineFingerprint("local", null, null).ShouldBe("local:bundled");
    }

    [Fact]
    public void EngineFingerprint_LocalWithModelPath_NamesThePath()
    {
        EmbeddingService.EngineFingerprint("local", "/models/custom.onnx", null).ShouldBe("local:/models/custom.onnx");
    }

    [Fact]
    public void EngineFingerprint_OpenAi_NamesModelAndEndpoint()
    {
        EmbeddingService.EngineFingerprint("openai", "nomic-embed-text", "http://localhost:11434")
            .ShouldBe("openai:nomic-embed-text@http://localhost:11434");
    }

    [Fact]
    public void EngineFingerprint_OpenAiWithoutBaseUrl_FallsBackToDefaultEndpoint()
    {
        EmbeddingService.EngineFingerprint("openai", "text-embedding-3-small", null)
            .ShouldBe("openai:text-embedding-3-small@https://api.openai.com/v1");
    }

    private static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var aSpan = a.Span;
        var bSpan = b.Span;
        double dot = 0;
        for (var i = 0; i < aSpan.Length; i++)
        {
            dot += aSpan[i] * bSpan[i];
        }

        return dot;
    }
}
