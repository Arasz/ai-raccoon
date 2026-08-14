using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Keeps tests/AiRaccoon.Tests/Resources/gate-query-vectors.json from drifting away from the
///     live embedding path without anyone noticing (docs/adr/0050): the fixture must cover exactly
///     the query catalog, and must carry the shas of the model that produced it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
public sealed class PinnedQueryVectorFixtureTests
{
    [Fact]
    public void Fixture_CoversExactlyTheQueryCatalog()
    {
        var pinned = PinnedQueryVectors.Load();
        var catalog = BaselineQueryCatalog.Load();

        var pinnedTexts = pinned.Vectors.Select(v => v.Query).ToHashSet(StringComparer.Ordinal);
        var catalogTexts = catalog.Select(q => q.Query).ToHashSet(StringComparer.Ordinal);

        catalogTexts.Except(pinnedTexts, StringComparer.Ordinal).ShouldBeEmpty(
            $"every query in {BaselineQueryCatalog.RelativePath} needs a pinned vector; regenerate with " +
            $"{GateQueryVectorRegenerationTool.RunEnvVar}=1");
        pinnedTexts.Except(catalogTexts, StringComparer.Ordinal).ShouldBeEmpty(
            $"the fixture pins query text no longer in {BaselineQueryCatalog.RelativePath}; regenerate with " +
            $"{GateQueryVectorRegenerationTool.RunEnvVar}=1");
        pinned.Vectors.Select(v => v.Id).ShouldBe(catalog.Select(q => q.Id), ignoreOrder: true);
    }

    /// <summary>
    ///     The fixture is only meaningful for the model that produced it: a re-quantized or swapped
    ///     model (any of docs/adr/0049's open options) changes these constants and fails here rather
    ///     than letting the sweeps keep scoring stale vectors against a new bank.
    /// </summary>
    [Fact]
    public void Fixture_RecordsTheModelThatProducedIt()
    {
        var pinned = PinnedQueryVectors.Load();

        pinned.ModelSha256.ShouldBe(BundledModel.ModelSha256,
            $"the fixture was generated from a different model; regenerate with {GateQueryVectorRegenerationTool.RunEnvVar}=1");
        pinned.VocabSha256.ShouldBe(BundledModel.VocabSha256,
            $"the fixture was generated from a different vocab; regenerate with {GateQueryVectorRegenerationTool.RunEnvVar}=1");
    }

    /// <summary>Provenance is the point of the fixture — an entry with no recorded arithmetic path
    /// or date is indistinguishable from ground truth to the next reader (docs/adr/0049).</summary>
    [Fact]
    public void Fixture_RecordsItsProvenance()
    {
        var pinned = PinnedQueryVectors.Load();

        pinned.ArithmeticPath.ShouldNotBeNullOrWhiteSpace();
        pinned.GeneratedOn.ShouldNotBeNullOrWhiteSpace();
        DateOnly.TryParse(pinned.GeneratedOn, out _).ShouldBeTrue($"GeneratedOn '{pinned.GeneratedOn}' must be a date");
        pinned.Note.ShouldContain("NOT ground truth");
        pinned.Note.ShouldContain("0049");
    }

    [Fact]
    public void Fixture_VectorsAreUnitNormAtTheDeclaredDimension()
    {
        var pinned = PinnedQueryVectors.Load();

        pinned.Dimension.ShouldBe(384);
        foreach (var entry in pinned.Vectors)
        {
            var vector = PinnedQueryVectors.Decode(entry.Vector);
            vector.Length.ShouldBe(pinned.Dimension, $"{entry.Id} must carry {pinned.Dimension} floats");
            PinnedQueryVectors.Fingerprint(entry.Vector).ShouldBe(entry.Sha256, $"{entry.Id} sha256 must match its bytes");
            Math.Sqrt(vector.Sum(v => (double)v * v)).ShouldBe(1.0, 1e-3, $"{entry.Id} must be L2-normalized");
        }
    }

    /// <summary>An unknown query must throw, not fall back to the live model — the fallback is what
    /// would let a changed catalog quietly reintroduce the host-CPU variable (docs/adr/0050).</summary>
    [Fact]
    public async Task PinnedService_ThrowsForAQueryTheFixtureDoesNotCarry()
    {
        var service = PinnedQueryVectors.EmbeddingService();
        var generator = service.CreateGenerator(new EmbeddingSettings("local", null, null, null));

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await generator.GenerateAsync(["a query no catalog entry contains"],
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PinnedService_ReturnsTheCommittedBytesVerbatim()
    {
        var pinned = PinnedQueryVectors.Load();
        var entry = pinned.Vectors.Single(v => v.Id == "A6");
        var generator = PinnedQueryVectors.EmbeddingService()
            .CreateGenerator(new EmbeddingSettings("local", null, null, null));

        var result = await generator.GenerateAsync([entry.Query],
            cancellationToken: TestContext.Current.CancellationToken);

        PinnedQueryVectors.Encode(result[0].Vector.Span).ShouldBe(entry.Vector);
    }
}
