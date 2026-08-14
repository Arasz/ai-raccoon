using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     IContentEmbedder adapter over IEntryEmbedder.EmbedQueryAsync (ADR-0039) — keeps the Core-layer
///     interface connection-free while reusing the bank's already-configured embedding engine rather
///     than re-implementing engine resolution. Runs against the real bundled MiniLM model, not a
///     fake: this is the "real bundled model" leg of the anti-fixed-vector test requirement.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteContentEmbedderTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-content-embedder");
    private readonly SqliteConnectionFactory _factory;
    private readonly EntryEmbedder _entryEmbedder = new(new EmbeddingService());

    public SqliteContentEmbedderTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task EmbedContentAsync_NoEngineConfigured_DegradesToNull()
    {
        var sut = new SqliteContentEmbedder(_factory, _entryEmbedder);

        var vector = await sut.EmbedContentAsync("anything", TestContext.Current.CancellationToken);

        vector.ShouldBeNull();
    }

    [Fact]
    public async Task EmbedContentAsync_EngineConfigured_ReturnsA384DimensionVector()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await _entryEmbedder.ConfigureAsync(connection, "local", BundledModel.ResolveModelPath(), null,
                TestContext.Current.CancellationToken);
        }

        var sut = new SqliteContentEmbedder(_factory, _entryEmbedder);

        var vector = await sut.EmbedContentAsync("a background process completed normally", TestContext.Current.CancellationToken);

        vector.ShouldNotBeNull();
        vector.Length.ShouldBe(384);
    }

    [Fact]
    public async Task EmbedContentAsync_DifferentContent_ProducesDifferentVectors()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await _entryEmbedder.ConfigureAsync(connection, "local", BundledModel.ResolveModelPath(), null,
                TestContext.Current.CancellationToken);
        }

        var sut = new SqliteContentEmbedder(_factory, _entryEmbedder);

        var a = await sut.EmbedContentAsync("the quick brown fox jumps over the lazy dog", TestContext.Current.CancellationToken);
        var b = await sut.EmbedContentAsync("architectural decision record about the write path", TestContext.Current.CancellationToken);

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        a.ShouldNotBe(b);
    }
}
