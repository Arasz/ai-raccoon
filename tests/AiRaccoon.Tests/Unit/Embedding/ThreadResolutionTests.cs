using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>WP11-A / G16: the ORT intra-op thread cap. Counts only — never wall-clock (owner ruling #464).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ThreadResolutionTests
{
    [Fact]
    public void Resolve_UnsetSetting_HalvesTheCoreCount()
    {
        var service = TestData.CreateEmbeddingService();

        service.ResolveThreadCount(null, 10).ShouldBe(5);
    }

    [Fact]
    public void Resolve_UnsetSetting_OneCore_NeverGoesBelowOne()
    {
        var service = TestData.CreateEmbeddingService();

        service.ResolveThreadCount(null, 1).ShouldBe(1);
    }

    [Fact]
    public void Resolve_ExplicitZero_MeansOrtDefault()
    {
        var service = TestData.CreateEmbeddingService();

        service.ResolveThreadCount("0", 10).ShouldBe(0);
    }

    [Fact]
    public void Resolve_Garbage_FallsBackToTheHalvedDefault()
    {
        var service = TestData.CreateEmbeddingService();

        service.ResolveThreadCount("not-a-number", 10).ShouldBe(5);
    }

    [Fact]
    public void Resolve_NegativeValue_IsGarbageToo()
    {
        var service = TestData.CreateEmbeddingService();

        service.ResolveThreadCount("-1", 10).ShouldBe(5);
    }

    [Fact]
    public void Resolve_ExplicitPositiveValue_IsUsedAsIs()
    {
        var service = TestData.CreateEmbeddingService();

        service.ResolveThreadCount("3", 10).ShouldBe(3);
    }

    /// <summary>Constructs a real ONNX session, so this one leg is Integration/Slow like every other OnnxEmbeddingGenerator ctor test.</summary>
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait(TestCategories.Speed, TestCategories.Slow)]
    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    public void Generator_ReportsTheIntraOpThreadsItWasBuiltWith(int threads)
    {
        using var generator = new OnnxEmbeddingGenerator(BundledModel.ResolveModelPath(),
            WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()),
            EmbeddingService.BundledDescriptor, NullLogger.Instance, threads);

        generator.IntraOpThreads.ShouldBe(threads);
    }
}
