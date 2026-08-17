using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>
///     The response variable ADR-0077 named as missing: a relevance set that survives the chunking
///     change under test. Anchoring on an answer span rather than a chunk id keeps the ground truth
///     defined when an arm deletes the chunk, and keeps it from growing when an arm fragments a file.
/// </summary>
public sealed class SpanAnchoredRelevanceTests
{
    private const string Expected = "docs/retrieval.md";

    private static CorpusChunk Chunk(string hash, string file, string value) => new(hash, file, value);

    [Fact]
    public void Resolve_SelectsOnlyTheChunkCarryingTheSpan()
    {
        var corpus = new[]
        {
            Chunk("h1", Expected, "| Bucket | What it means |\n| clipped | The stored text hit the 200-char field cap |"),
            Chunk("h2", Expected, "unrelated prose about fixture bias and telemetry harvesting")
        };

        var relevant = SpanAnchoredRelevance.Resolve(corpus, Expected, "The stored text hit the 200-char field cap");

        relevant.ShouldBe(new HashSet<string> { "h1" });
    }

    [Fact]
    public void Resolve_IgnoresTheSpanWhenItAppearsInAnotherFile()
    {
        var corpus = new[]
        {
            Chunk("h1", "docs/other.md", "| clipped | The stored text hit the 200-char field cap |"),
            Chunk("h2", Expected, "| clipped | The stored text hit the 200-char field cap |")
        };

        var relevant = SpanAnchoredRelevance.Resolve(corpus, Expected, "The stored text hit the 200-char field cap");

        relevant.ShouldBe(new HashSet<string> { "h2" });
    }

    /// <summary>
    ///     The property that makes this metric adjudicable. ADR-0077 rejected the proposed measurement
    ///     partly because two arms multiply the units containing the answer, inflating any
    ///     rank-of-any-match score mechanically. Splitting a table into one chunk per row must not
    ///     enlarge the relevance set.
    /// </summary>
    [Fact]
    public void Resolve_DoesNotGrowWhenAnArmFragmentsTheFile()
    {
        const string span = "The stored text hit the 200-char field cap";
        var wholeTable = new[]
        {
            Chunk("w1", Expected, $"| Bucket | What it means |\n| clipped | {span} |\n| missing | The row was never written |")
        };
        var perRow = new[]
        {
            Chunk("r1", Expected, "| Bucket | What it means |"),
            Chunk("r2", Expected, $"| Bucket | What it means |\n| clipped | {span} |"),
            Chunk("r3", Expected, "| Bucket | What it means |\n| missing | The row was never written |")
        };

        SpanAnchoredRelevance.Resolve(wholeTable, Expected, span).Count.ShouldBe(1);
        SpanAnchoredRelevance.Resolve(perRow, Expected, span).Count.ShouldBe(1);
    }

    [Fact]
    public void Resolve_MatchesAcrossWhitespaceAndLineBreakDifferences()
    {
        var corpus = new[]
        {
            Chunk("h1", Expected, "|  clipped   |  The stored text hit\n   the 200-char field cap  |")
        };

        var relevant = SpanAnchoredRelevance.Resolve(corpus, Expected, "The stored text hit the 200-char field cap");

        relevant.ShouldBe(new HashSet<string> { "h1" });
    }

    [Fact]
    public void Resolve_MatchesTheExpectedFileBySuffix()
    {
        var corpus = new[] { Chunk("h1", "/tmp/extract/docs/retrieval.md", "| clipped | the cap applies |") };

        SpanAnchoredRelevance.Resolve(corpus, Expected, "the cap applies").ShouldBe(new HashSet<string> { "h1" });
    }

    /// <summary>
    ///     A ground-truth anchor no chunk carries scores 0 on every arm, which reads as "retrieval is
    ///     bad" when it means "the measurement is broken". It must fail loudly instead.
    /// </summary>
    [Fact]
    public void Resolve_ThrowsWhenNoChunkCarriesTheSpan()
    {
        var corpus = new[] { Chunk("h1", Expected, "| clipped | some other definition |") };

        var thrown = Should.Throw<InvalidOperationException>(() =>
            SpanAnchoredRelevance.Resolve(corpus, Expected, "The stored text hit the 200-char field cap"));

        thrown.Message.ShouldContain("no chunk of");
    }

    [Fact]
    public void Resolve_ThrowsWhenTheExpectedFileIsAbsentFromTheCorpus()
    {
        var corpus = new[] { Chunk("h1", "docs/other.md", "| clipped | the cap applies |") };

        Should.Throw<InvalidOperationException>(() =>
            SpanAnchoredRelevance.Resolve(corpus, Expected, "the cap applies"));
    }
}
