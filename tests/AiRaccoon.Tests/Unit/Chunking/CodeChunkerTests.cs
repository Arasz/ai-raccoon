using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     WP2 (docs/work/2026-08-21-code-search-implementation-plan.md §3.4): line-range splitter —
///     blank-line blocks with brace-balance boundary preference, per-line hard-split floor, exact
///     joined-recount packing (ADR-0036), overlay 0. Unit tests use a character-counting fake
///     tokenizer (repo pattern: <c>CharCount</c> in <c>MarkdownChunkerTests</c>); WP2-T11 is the
///     one integration case pinning the real sentencepiece arithmetic
///     (<c>Integration/ChunkBudgetIsEngineAwareTests.cs</c>).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CodeChunkerTests
{
    private static int CharCount(string text) => text.Length;

    private static CodeChunker Chunker(int budget) => new(new FakeCodeTokenizer(CharCount), budget);

    /// <summary>Three self-contained top-level functions, no blank lines: each `{ }` pair returns
    /// to brace-balance 0 at its own close, so zero-crossings recur without any blank-line help.</summary>
    private static string ThreeFunctionsNoBlankLines() =>
        string.Concat(Enumerable.Range(1, 3).Select(i => $"void M{i}()\n{{\n    DoWork();\n}}\n"));

    /// <summary>Mirrors CodeChunker's own SplitLines: each line keeps its trailing '\n' except a
    /// possible final unterminated line; no phantom empty final element for a trailing '\n'.</summary>
    private static List<string> SplitIntoChunkerLines(string text)
    {
        List<string> result = [];
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            result.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length)
        {
            result.Add(text[start..]);
        }

        return result;
    }

    private static int BraceBalance(string text) => text.Count(c => c == '{') - text.Count(c => c == '}');

    [Fact]
    public void Chunk_NoChunkExceedsThe126TokenBudget()
    {
        var lines = new List<string>
        {
            "public int Add(int a, int b)",
            "{",
            "    return a + b;",
            "}",
            "",
            "public string BuildDescription(int a, int b, int c, int d, int e, int f, int g, int h)",
            "{",
            "    var result = a + b + c + d + e + f + g + h;",
            "    var doubled = result * 2;",
            "    var tripled = result * 3;",
            "    return $\"{result}-{doubled}-{tripled}-more-text-to-push-this-well-past-one-hundred-twenty-six-chars\";",
            "}",
            "",
            "public void Small() { }"
        };
        var text = string.Join('\n', lines) + "\n";

        var chunks = Chunker(CodeChunker.DefaultBudget).Chunk(text);

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => CharCount(c.Text) <= CodeChunker.DefaultBudget);
    }

    [Fact]
    public void Budget_IsCtxMinusTwo_NotTheMemory254()
    {
        CodeChunker.DefaultBudget.ShouldBe(126,
            "code-daemon-embed-v1: ctx 128 - reservation 2 (plan §12.1 H3)");
        CodeChunker.DefaultBudget.ShouldNotBe(254, "254 is the memory bundled-model budget, not code's");

        var overBudget = Chunker(CodeChunker.DefaultBudget).Chunk(new string('x', 200) + "\n");
        overBudget.Count.ShouldBeGreaterThanOrEqualTo(2, "a 200-char line exceeds the 126 budget and must split");

        var underBudget = Chunker(CodeChunker.DefaultBudget).Chunk(new string('y', 120) + "\n");
        underBudget.Count.ShouldBe(1, "a 120-char line fits the 126 budget in one chunk");
    }

    [Fact]
    public void Chunk_LineRanges_AreContiguousAndCoverTheFile()
    {
        var contentLines = Enumerable.Range(1, 40)
            .Select(i => i % 5 == 0 ? "" : $"statement{i:D2}();")
            .ToList();
        var text = string.Join('\n', contentLines) + "\n";

        var chunks = Chunker(60).Chunk(text);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks[0].LineStart.ShouldBe(1);
        chunks[^1].LineEnd.ShouldBe(40);
        for (var i = 1; i < chunks.Count; i++)
        {
            chunks[i].LineStart.ShouldBe(chunks[i - 1].LineEnd + 1, "ranges must be contiguous, no gaps");
        }
    }

    [Fact]
    public void Chunk_LineRanges_WithAHardSplitLine_StillCoverTheFile_OnlyTheSplitLineRepeats()
    {
        var lines = new List<string> { "a();", "b();", new string('z', 300), "c();" };
        var text = string.Join('\n', lines) + "\n";

        var chunks = Chunker(CodeChunker.DefaultBudget).Chunk(text);

        var allLineNumbers = chunks
            .SelectMany(c => Enumerable.Range(c.LineStart, c.LineEnd - c.LineStart + 1))
            .ToList();
        allLineNumbers.Min().ShouldBe(1);
        allLineNumbers.Max().ShouldBe(lines.Count);
        Enumerable.Range(1, lines.Count).ShouldAllBe(n => allLineNumbers.Contains(n), "union must cover 1..N");

        var repeated = allLineNumbers.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        repeated.ShouldBe([3], "only the hard-split line (3) may appear in more than one range");
    }

    [Fact]
    public void Chunk_BlankLines_ArePreferredSplitPoints()
    {
        var blocks = new[] { "void A() { }", "void B() { }", "void C() { }", "void D() { }", "void E() { }" };
        var text = string.Join("\n\n", blocks) + "\n";

        var chunks = Chunker(30).Chunk(text);
        var chunkerLines = SplitIntoChunkerLines(text);

        chunks.Count.ShouldBeGreaterThan(1, "the fixture must force multiple chunks to test boundary placement");
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            string.IsNullOrWhiteSpace(chunkerLines[chunk.LineEnd - 1]).ShouldBeTrue(
                $"chunk boundary at line {chunk.LineEnd} must sit on a blank line, not mid-function");
        }
    }

    [Fact]
    public void Chunk_BraceBalance_DelaysSplitsInsideUnbalancedRegions()
    {
        var text = ThreeFunctionsNoBlankLines();
        var chunkerLines = SplitIntoChunkerLines(text);

        var chunks = Chunker(20).Chunk(text);

        chunks.Count.ShouldBeGreaterThan(1);
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            var throughBoundary = string.Concat(chunkerLines.Take(chunk.LineEnd));
            BraceBalance(throughBoundary).ShouldBe(0,
                $"boundary at line {chunk.LineEnd} must land at brace-depth 0 (no blank lines to fall back on)");
        }
    }

    [Fact]
    public void Chunk_SingleLineOverflow_HardSplitsTheLine()
    {
        var text = new string('x', 300) + "\n";

        var chunks = Chunker(CodeChunker.DefaultBudget).Chunk(text);

        chunks.Count.ShouldBeGreaterThanOrEqualTo(3);
        chunks.ShouldAllBe(c => CharCount(c.Text) <= CodeChunker.DefaultBudget);
        string.Concat(chunks.Select(c => c.Text)).ShouldBe(text,
            "concatenating chunk values must reproduce the original line");
        chunks.ShouldAllBe(c => c.LineStart == 1 && c.LineEnd == 1,
            "the whole file is one line, hard-split across chunks");
    }

    [Fact]
    public void Chunk_ChunkIndexAndTotalChunks_AreContiguousAndDeterministic()
    {
        var text = ThreeFunctionsNoBlankLines();
        var chunker = Chunker(20);

        var first = chunker.Chunk(text);
        var second = chunker.Chunk(text);

        first.ShouldBe(second, "identical input must yield identical chunk accounting (stable hashes downstream)");
        first.Select((_, i) => i).ShouldBe(Enumerable.Range(0, first.Count),
            "chunk positions are contiguous 0..N-1 by construction");
    }

    [Fact]
    public void Chunk_EmptyFile_ProducesNoChunks()
    {
        var chunker = Chunker(CodeChunker.DefaultBudget);

        chunker.Chunk("").ShouldBeEmpty();
        chunker.Chunk("\n\n  \n").ShouldBeEmpty();
    }

    [Fact(Timeout = 5000)]
    public void Chunk_NoSplitPoints_StillBoundedAndComplete()
    {
        var lines = new List<string> { "public class Dense", "{", "    void Big()", "    {" };
        lines.AddRange(Enumerable.Range(0, 40).Select(i => $"        statement{i:D2} = compute({i});"));
        lines.Add("    }");
        lines.Add("}");
        var text = string.Join('\n', lines) + "\n";

        var chunks = Chunker(CodeChunker.DefaultBudget).Chunk(text);

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(c => CharCount(c.Text) <= CodeChunker.DefaultBudget);
        chunks[0].LineStart.ShouldBe(1);
        chunks[^1].LineEnd.ShouldBe(lines.Count);
        for (var i = 1; i < chunks.Count; i++)
        {
            chunks[i].LineStart.ShouldBe(chunks[i - 1].LineEnd + 1);
        }
    }

    [Fact]
    public void Chunk_NoOverlay_LineRangesAreDisjoint()
    {
        var text = ThreeFunctionsNoBlankLines();

        var chunks = Chunker(20).Chunk(text);

        var allLineNumbers = chunks
            .SelectMany(c => Enumerable.Range(c.LineStart, c.LineEnd - c.LineStart + 1))
            .ToList();
        allLineNumbers.ShouldBe(allLineNumbers.Distinct(), "no hard-split lines in this fixture: no line may repeat");
    }

    private sealed class FakeCodeTokenizer(Func<string, int> count) : ICodeTokenizer
    {
        public int CountTokens(string text) => count(text);
    }
}
