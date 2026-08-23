using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     Adversarial coverage for the #538/#489 deferral rule (QA of PR #543,
///     qa-543/QA-REVIEW.md + AdversarialCases.cs). Fix-pinning and regression-guard tests for the
///     deferral rule's behaviour matrix; kept separate from <see cref="MarkdownChunkerTests" />
///     because that file already covers the chunker's non-deferral behaviour.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MarkdownChunkerDeferralTests
{
    private static int CharCount(string text) => text.Length;

    /// <summary>A crude non-char tokenizer: word pieces of 4 chars plus one token per newline.
    /// Token counts are not proportional to length under it, unlike CharCount.</summary>
    private static int WordPieces(string text)
    {
        var pieces = 0;
        foreach (var word in text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            pieces += (word.Length + 3) / 4;
        }

        return pieces + text.Count(c => c == '\n');
    }

    private static bool IsHeadingLine(string line)
    {
        var trimmed = line.TrimStart();
        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' '
               && trimmed[(level + 1)..].Trim().Length > 0;
    }

    private static bool IsFenceLine(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal)
        || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal);

    /// <summary>True when the chunk carries no body text at all — only headings and blank lines.
    /// Such a chunk still becomes an entries row and an embedding in FileIngestor.</summary>
    private static bool HasNoBodyText(string chunk)
    {
        var inFence = false;
        foreach (var line in chunk.Split('\n'))
        {
            if (IsFenceLine(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                if (line.Trim().Length > 0)
                {
                    return false;
                }

                continue;
            }

            if (line.Trim().Length == 0 || IsHeadingLine(line))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------
    // Pins the fix: BREAK-1 — deferral must not leave a content-free chunk (QA gate on 93756cb6)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Split_FirstSectionLargerThanMaxTokens_DoesNotEmitAContentFreeTitleChunk()
    {
        // BREAK-1: heading is the chunk's only surviving new unit after deferral. The document's
        // first section does not fit one chunk, so DeferOpenSection used to hand the heading and
        // all its body to the next chunk, leaving chunk 0 holding "# Title\n\n" and nothing else —
        // an entries row with section='Title' and no indexable content. Deferral must not fire
        // when nothing but headings and blanks would remain.
        var text = "# Title\n\n## Big\nLINE1\nLINE2\nLINE3\nLINE4\nLINE5\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 30);

        chunks.ShouldAllBe(chunk => !HasNoBodyText(chunk));
        chunks[0].ShouldNotBe("# Title\n\n");
    }

    [Fact]
    public void Split_ContentFreeChunkIsNeverAPrefixOfTheNextChunk()
    {
        // BREAK-1, production shape: maxTokens 512 / overlay 48, the real
        // ChunkingDefaults.OverlayTokens. Chunk 0 used to be "# Harbor Manual\n\n" and chunk 1's
        // overlay re-emitted exactly that text, so chunk 0 was a wholly redundant row carrying
        // nothing chunk 1 didn't already carry.
        var tide = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"Tide line {i} with a handful of words of prose in it."));
        var registry = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"Registry line {i} with a handful of words of prose."));
        var text = $"# Harbor Manual\n\n## Tide Correction\n{tide}\n\n## Boat Registry Cleanup\n{registry}\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 512, ChunkingDefaults.OverlayTokens);

        for (var i = 0; i + 1 < chunks.Count; i++)
        {
            chunks[i + 1].StartsWith(chunks[i], StringComparison.Ordinal)
                .ShouldBeFalse($"chunk[{i}] is re-emitted whole as the head of chunk[{i + 1}]");
        }
    }

    [Fact]
    public void Split_HeadingLadderThenLongBody_DoesNotEmitOneChunkPerHeading()
    {
        // BREAK-1: consecutive nested headings, deferral cascade. The do/while cascade in
        // BuildChunk used to defer Section D, then C, then B, until chunk 0 was the single line
        // "# A\n".
        var text = "# A\n## B\n### C\n#### D\nLINE1\nLINE2\nLINE3\nLINE4\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 30);

        chunks.ShouldAllBe(chunk => !HasNoBodyText(chunk));
    }

    [Fact]
    public void Split_LeadingBlankLineBeforeTheFirstHeading_DoesNotEmitAWhitespaceOnlyChunk()
    {
        // BREAK-1, extra shape over Split_DeferralWouldLeaveOnlyBlankNewUnits_… (MarkdownChunkerTests):
        // a leading blank line makes the document's first heading stop being the chunk's first new
        // unit, so the "never defer the first new unit" guard no longer protects it on its own —
        // everything used to defer and chunk 0 was the blank line alone.
        var text = "\n# Title\n\nLINE1\nLINE2\nLINE3\nLINE4\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 24);

        chunks.ShouldAllBe(chunk => chunk.Trim().Length > 0);
    }

    // ---------------------------------------------------------------------------------------
    // Pins the fix: BREAK-3 — the sub-fence floor check must include the forced content newline
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Split_SubFenceSplitting_NeverExceedsMaxTokens()
    {
        // BREAK-3, pre-existing, unrelated to #538/#489. FlushAsSubFences decided whether
        // sub-fencing was possible with countTokens(opener + closer) >= maxTokens, but the
        // smallest sub-fence it can actually emit is opener + a forced content newline + closer
        // (SubFenceLines always EndsWithNewline the content slot, even when empty). At
        // maxTokens=9, "```\ncode 0\n```\n" used to emit six 10-token chunks — a floor-check
        // off-by-one, not an impossible budget (green at 10).
        var chunks = new MarkdownChunker(CharCount).Chunk("```\ncode 0\n```\n", 9);

        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 9);
    }

    [Fact]
    public void Split_SubFenceSplittingOneTokenAboveTheFloor_StaysWithinBudget()
    {
        // Green control for the test above: the same input one token higher was already correct,
        // which is what makes BREAK-3 a bug rather than an impossible budget.
        var chunks = new MarkdownChunker(CharCount).Chunk("```\ncode 0\n```\n", 10);

        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 10);
    }

    // ---------------------------------------------------------------------------------------
    // Regression guards: currently correct, previously unpinned
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("### Sub", 41)]           // level 3 — HeadingPathParser ignores levels > 2
    [InlineData("###### Six", 44)]        // level 6 — far end of the ignored range
    [InlineData("## Source: x/y.md", 51)] // provenance header — parser treats it as metadata
    public void Split_HeadingThatCannotChangeALabel_IsNotTreatedAsASectionOpener(string heading, int maxTokens)
    {
        // HeadingPathParser drops levels 3-6 and '## Source:' (docs/adr/0004), so deferring one of
        // them cannot fix any label — it would only shrink the chunk and hand the next one a null
        // section. The heading must stay with the level-1/2 section whose content surrounds it.
        var text = $"# Title\n\n## Section A\nAAAA\n\n{heading}\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, maxTokens);

        var chunk = chunks.Single(c => c.Contains(heading, StringComparison.Ordinal));
        chunk.ShouldContain("## Section A");
        HeadingPathParser.Parse(chunk).ShouldEndWith("Section A");
    }

    [Theory]
    [InlineData("####### Seven", 51)] // seven hashes: not a heading at all
    [InlineData("#NoSpace", 46)]      // no space after the hash
    [InlineData("##   ", 43)]         // heading marker with blank text
    public void Split_HeadingLookalikeAtChunkTail_IsNeverTreatedAsAHeading(string lookalike, int maxTokens)
    {
        var text = $"# Title\n\n## Section A\nAAAA\n\n{lookalike}\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, maxTokens);

        chunks.Single(chunk => chunk.Contains(lookalike, StringComparison.Ordinal))
            .ShouldContain("## Section A");
    }

    [Fact]
    public void Split_UnicodeHeadings_DeferTheSameWayAsAsciiOnes()
    {
        var text = "# Tytuł\n\n## Sekcja Ż\nAAAA\n\n## Następna\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 44);

        HeadingPathParser.Parse(chunks[0]).ShouldBe("Tytuł > Sekcja Ż");
        chunks[1].ShouldStartWith("## Następna");
    }

    [Fact]
    public void Split_HeadingsWithTrailingSpaces_DeferTheSameWayAsTrimmedOnes()
    {
        var text = "# Title\n\n## Section A   \nAAAA\n\n## Section B   \nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 50);

        HeadingPathParser.Parse(chunks[0]).ShouldBe("Title > Section A");
        chunks[1].ShouldStartWith("## Section B");
    }

    [Fact]
    public void Split_ConsecutiveHeadingsHashHashThenHash_LabelsTheChunkWithTheDeeperLastHeading()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\n# Section C\nCCCC\nDDDD\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 58);

        HeadingPathParser.Parse(chunks[0]).ShouldBe("Title > Section A");
        HeadingPathParser.Parse(chunks[1]).ShouldBe("Section C");
    }

    [Fact]
    public void Split_FenceIsTheUnitAfterTheBoundary_CountsAsProofTheSectionContinues()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\n```\ncode\n```\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 30);

        chunks[0].ShouldBe("# Title\n\n## Section A\nAAAA\n\n");
        chunks[1].ShouldStartWith("## Section B");
    }

    [Fact]
    public void Split_FenceContainingAHashLineAtTheTail_IsNeverMistakenForAHeading()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n```\n# not a heading\n```\nTAIL\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 40);

        chunks.Single(chunk => chunk.Contains("# not a heading", StringComparison.Ordinal))
            .ShouldNotStartWith("# not a heading");
    }

    [Fact]
    public void Split_OverlayCarryingTheDeferredHeadingsSection_StillLabelsTheNextChunkCorrectly()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 30, 14);

        HeadingPathParser.Parse(chunks[0]).ShouldBe("Title > Section A");
        HeadingPathParser.Parse(chunks[1]).ShouldBe("Section B");
        chunks[1].ShouldContain("BBBB");
    }

    [Fact]
    public void Split_CrLfInputWithADeferredHeading_ProducesTheSameChunksAsLf()
    {
        var lf = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\nCCCC\n";

        var crlf = new MarkdownChunker(CharCount).Chunk(lf.Replace("\n", "\r\n"), 46);

        crlf.ShouldBe(new MarkdownChunker(CharCount).Chunk(lf, 46));
    }

    [Fact]
    public void Split_NoTrailingNewlineAfterTheLastBodyLine_StillDefersCorrectly()
    {
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\nCCCC";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 46);

        chunks[0].ShouldBe("# Title\n\n## Section A\nAAAA\n\n");
        chunks[1].ShouldBe("## Section B\nBBBB\nCCCC");
    }

    [Fact]
    public void Split_ChunkingAChunkAgain_ReturnsThatChunkUnchanged()
    {
        // Idempotence: a chunk already within budget must not be re-split, or a re-ingest of an
        // already-chunked value would drift.
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 46);

        foreach (var chunk in chunks)
        {
            new MarkdownChunker(CharCount).Chunk(chunk, 46).ShouldBe([chunk]);
        }
    }

    // ---------------------------------------------------------------------------------------
    // #549/#550 shapes the deferral rule cannot reach — the label is fixed one layer up, by
    // ChunkWithHeadings (docs/adr/0048): a chunk that only opens a section claims none, and the
    // section's body chunks carry its path however the text was cut.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ChunkWithHeadings_HugeLineRightAfterAHeading_BodyChunksCarryTheSectionTheHeadingOpened()
    {
        // #549: the 200-char line cannot sit beside "## Section A", so the text is cut mid-word
        // into headerless chunks; their label comes from the heading context, not their text.
        var text = "# Title\n\n## Section A\n" + new string('x', 200) + "\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 30);

        chunks.Where(c => c.Text.Contains('x')).ShouldAllBe(c => c.HeadingPath == "Title > Section A");
        chunks.Where(c => c.Text.Contains('x')).ShouldAllBe(c => c.Sections.SequenceEqual(new[] { "Section A" }));
    }

    [Fact]
    public void ChunkWithHeadings_ProvenanceHeaderThenOversizedFirstSection_HeaderOnlyChunkClaimsNoSection()
    {
        // #549: the headers alone exhaust the budget, so chunk 0 stays header-only. It claims no
        // section; the body chunk carries Title.
        var text = "## Source: docs/manual.md\n\n# Title\n\nLINE1\nLINE2\nLINE3\nLINE4\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 40);

        chunks[0].Text.ShouldBe("## Source: docs/manual.md\n\n# Title\n\n");
        chunks[0].Sections.ShouldBeEmpty();
        chunks[1].HeadingPath.ShouldBe("Title");
    }

    [Fact]
    public void ChunkWithHeadings_UnderANonCharTokenizer_StrandedBodyStillCarriesItsSection()
    {
        // #549 under a non-linear tokenizer (docs/adr/0036): the label must not depend on
        // CharCount's linearity.
        var text = "# Title\n\n## Section A\nAAAA words here\n\n## Section B\nBBBB more words\nCCCC yet more\n";

        var chunks = new MarkdownChunker(WordPieces).ChunkWithHeadings(text, 14, 0, WordPieces);

        chunks.ShouldAllBe(c => WordPieces(c.Text) <= 14);
        chunks.Where(c => c.Text.Contains("BBBB") || c.Text.Contains("CCCC"))
            .ShouldAllBe(c => c.Sections.SequenceEqual(new[] { "Section B" }));
        chunks.Where(c => !HasNoBodyText(c.Text)).ShouldAllBe(c => c.Sections.Count > 0);
    }

    [Fact]
    public void ChunkWithHeadings_HeadingOnlyDocument_EmitsItsHeadingsWithoutClaimingASection()
    {
        // #550: a document that is only headings has nothing to label; its chunk is emitted
        // (the text exists) but claims no section — it is not a lie, just a heading list.
        var text = "# H1\n# H2\n# H3\n# H4\n# H5\n# H6\n";

        var chunks = new MarkdownChunker(CharCount).ChunkWithHeadings(text, 25, 10);

        chunks.ShouldAllBe(c => c.Sections.Count == 0);
        chunks.ShouldAllBe(c => c.HeadingPath.Length == 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Split_AtDegenerateBudgets_TerminatesAndStaysWithinBudget(int maxTokens)
    {
        // #550 (BREAK-6, pre-existing, low severity, not fixed here). Splitting terminates and
        // stays within budget at a budget below one line, but the de-fence/line-split fallback
        // (not the deferral path 93756cb6 closed) still emits bare whitespace-only chunks such as
        // "\n" or " ".
        var chunks = new MarkdownChunker(CharCount).Chunk("# Title\n\n## Section A\nAAAA\n", maxTokens);

        chunks.ShouldAllBe(chunk => CharCount(chunk) <= maxTokens);
    }

    [Fact]
    public void Split_SectionWhoseOnlyTailIsASubHeading_StillDefersAsHavingNoContentOfItsOwn()
    {
        // Copilot round 4: a "###" line or a "## Source:" line after the opener is not content, so
        // the opener has nothing of its own and defers like a bare heading (#489 rule).
        // Chunk 0's budget (35) ends right after "### Sub\n\n"; the next unit is "## C", so the
        // section does not continue past the chunk — only the no-content-of-its-own rule can defer it.
        var text = "# T\n\n## A\nAAAA\nAAAA\n\n## B\n### Sub\n\n## C\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 35);

        chunks[0].ShouldBe("# T\n\n## A\nAAAA\nAAAA\n\n");
        chunks[1].ShouldStartWith("## B\n### Sub\n");
    }
}
