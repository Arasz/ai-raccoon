using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     #549/#550: the chunker reports the heading path it already knows instead of a caller
///     re-parsing chunk text after the fact. Chunk text, boundaries, budgets and hashes are
///     unchanged (docs/adr/0036) — only <see cref="TextChunk.HeadingPath" /> changes shape.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MarkdownChunkerHeadingPathTests
{
    private static int CharCount(string text) => text.Length;

    [Fact]
    public void ChunkWithHeadings_ContinuationChunkOfALongSection_CarriesThatSectionsPath()
    {
        // #549: continuation chunks of a section longer than one chunk used to carry no heading
        // at all (the heading lives only in the first chunk / its overlay), so a file#section
        // anchor resolved 1 of N chunks. Every chunk of every section must now carry that
        // section's path, no matter how many chunks the section spans.
        var tide = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"Tide line {i} with a handful of words of prose in it."));
        var registry = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"Registry line {i} with a handful of words of prose."));
        var text = $"# Harbor Manual\n\n## Tide Correction\n{tide}\n\n## Boat Registry Cleanup\n{registry}\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 200, 20);

        chunks.Count.ShouldBeGreaterThan(2, "the fixture is sized to span several chunks per section");
        chunks.ShouldAllBe(chunk => chunk.HeadingPath == "Harbor Manual > Tide Correction" || chunk.HeadingPath == "Harbor Manual > Boat Registry Cleanup");
    }

    [Fact]
    public void ChunkWithHeadings_OversizedFenceAfterAHeading_LabelsTheFenceChunksWithThatSection()
    {
        // #549 (BREAK-2): the greedy pack stops right after "## Section B" because the 12-line
        // fence cannot fit beside it, and DeferOpenSection cannot move the heading (first new
        // unit, progress guard) — so the fence's own chunks used to carry no section at all. The
        // one-pass context builder tracks the heading stack across ALL units regardless of chunk
        // boundaries, so the fence chunks still know they are under "Section B".
        var code = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"code line {i} of the block"));
        var text = $"# Title\n\n## Section A\nAAAA\n\n## Section B\n```\n{code}\n```\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 120);

        var fenceChunks = chunks.Where(c => c.Text.Contains("code line 0", StringComparison.Ordinal)).ToList();
        fenceChunks.ShouldNotBeEmpty();
        fenceChunks.ShouldAllBe(c => c.HeadingPath == "Title > Section B");
    }

    [Fact]
    public void ChunkWithHeadings_AChunkThatOnlyOpensASection_CarriesNoHeadingPath()
    {
        // #549 positively stated: a chunk that only opens a section (no contentful unit of its
        // own — the fence that would hold Section B's content is entirely in later chunks) claims
        // none, rather than falsely claiming the section whose content it does not hold.
        var code = string.Join("\n", Enumerable.Range(0, 12).Select(i => $"code line {i} of the block"));
        var text = $"# Title\n\n## Section A\nAAAA\n\n## Section B\n```\n{code}\n```\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 120);

        chunks.Single(c => c.Text == "## Section B\n").HeadingPath.ShouldBe("");
    }

    [Fact]
    public void ChunkWithHeadings_OverlayNearlyFillingTheBudget_NeverNamesASectionTheChunkOnlyOpens()
    {
        // #550 (BREAK-4): when the overlay leaves room for only one new unit and that unit is a
        // heading, the chunk's own content ("AAAA") belongs to Section A, not Section B — overlay
        // units are never eligible to supply the chunk's own contentful-unit label, so with no
        // contentful NEW unit the chunk claims no section rather than falsely claiming B.
        var text = "## Section A\nAAAA\n\n## Section B\nBBBB\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 20, 19);

        chunks.Single(c => c.Text == "AAAA\n\n## Section B\n").HeadingPath.ShouldBe("");
    }

    [Fact]
    public void ChunkWithHeadings_MinimalRepro_LabelsTheCodeChunkNotTheHeadingChunk()
    {
        // Minimal #549 repro: the heading alone fills chunk 0, the never-closed fence becomes
        // chunk 1 entirely on its own. Chunk 0 opens H0 but holds none of it; chunk 1 holds all of
        // it.
        var text = "# H0\n```\ncode 1";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 19);

        chunks.Select(c => c.HeadingPath).ShouldBe(["", "H0"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Split_AtDegenerateBudgets_EmitsNoWhitespaceOnlyChunk(int maxTokens)
    {
        // Rule D (#550-3): a chunk whose text is entirely whitespace is never emitted by
        // ChunkWithHeadings — only whitespace is ever dropped, so real content never disappears.
        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings("# T\n\nab cd\n", maxTokens);

        chunks.ShouldAllBe(c => c.Text.Trim().Length > 0 && CharCount(c.Text) <= maxTokens);
    }

    // ---------------------------------------------------------------------------------------
    // Regression guards: paths for shapes #489/#538 already handle correctly
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ChunkWithHeadings_HeadingWouldDangleAtChunkTail_StillDefersAndLabelsCorrectly()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 41);

        chunks.Select(c => c.Text).ShouldBe(["# Title\n\n## Section A\nAAAA\n\n", "## Section B\nBBBB\n"]);
        chunks.Select(c => c.HeadingPath).ShouldBe(["Title > Section A", "Title > Section B"]);
    }

    [Fact]
    public void ChunkWithHeadings_SectionContinuesPastChunkTail_StillDefersAndLabelsCorrectly()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 46);

        chunks.Select(c => c.Text).ShouldBe(["# Title\n\n## Section A\nAAAA\n\n", "## Section B\nBBBB\nCCCC\n"]);
        chunks.Select(c => c.HeadingPath).ShouldBe(["Title > Section A", "Title > Section B"]);
    }

    [Fact]
    public void ChunkWithHeadings_SubHeadingContinuation_InheritsTheParentSectionsPath()
    {
        // "### Sub" is neither pushed nor popped (HeadingPathParser only tracks levels 1-2), so a
        // chunk holding it still reports the level-1/2 section it falls under.
        var text = "# Title\n\n## Section A\nAAAA\n\n### Sub\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 100);

        chunks.ShouldHaveSingleItem();
        chunks[0].HeadingPath.ShouldBe("Title > Section A");
    }

    [Fact]
    public void ChunkWithHeadings_HashLineInsideAFence_NeverEntersTheHeadingStack()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n```\n# not a heading\n```\nTAIL\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 100);

        chunks.ShouldAllBe(c => c.HeadingPath == "Title > Section A");
    }

    [Fact]
    public void ChunkWithHeadings_TextMatchesChunkMinusWhitespaceOnlyChunks()
    {
        var text = "# T\n\n## A\nAAAAAAAA\n\n## B\nBBBB\nCCCC\nDDDD\n";
        var chunker = new MarkdownChunker(CharCount);

        var withHeadings = chunker.ChunkWithHeadings(text, 19);
        var plain = chunker.Chunk(text, 19);

        withHeadings.Select(c => c.Text).ShouldBe(plain.Where(c => c.Trim().Length > 0));
    }

    [Fact]
    public void ChunkWithHeadings_HeadingOnlyDocument_NeitherChunkClaimsASection()
    {
        var text = "# H1\n# H2\n# H3\n# H4\n# H5\n# H6\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 25, 10);

        chunks.ShouldAllBe(c => c.HeadingPath == "");
    }
}
