using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     The two correctness properties ADR-0077 separated from its tuning questions: prose and tables
///     never share a chunk, and no table body row is emitted without its header. Both are
///     deterministic and gateable on the text alone — no retrieval corpus is involved.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TableChunkingCorrectnessTests
{
    private static int CharCount(string text) => text.Length;

    private static IReadOnlyList<string> LinesOf(string chunk) =>
        chunk.Split('\n').Where(line => line.Trim().Length > 0).ToList();

    private static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    private static bool IsSeparator(string line) =>
        IsTableLine(line) && line.Trim('|', ' ', '\t', '-', ':', '\r').Length == 0 && line.Contains('-');

    private const string Header = "| Bucket | What it means |\n";
    private const string Separator = "|---|---|\n";

    private static string TableWith(int rows) =>
        Header + Separator + string.Concat(Enumerable.Range(1, rows)
            .Select(i => $"| term{i} | definition number {i} spelled out at length |\n"));

    /// <summary>
    ///     ADR-0077's motivating chunk mixed ~150 words of prose with a four-row table, so roughly
    ///     seven eighths of its embedded text was unrelated to the row that answered the query.
    /// </summary>
    [Fact]
    public void ProseAndTable_NeverShareAChunk()
    {
        var text = "Some prose about fixture bias.\nMore prose on telemetry harvesting.\n\n"
                   + TableWith(3)
                   + "\nTrailing prose that follows the table.\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 4000);

        var mixed = chunks.Where(chunk =>
            LinesOf(chunk).Any(IsTableLine) && LinesOf(chunk).Any(line => !IsTableLine(line))).ToList();
        mixed.ShouldBeEmpty($"a chunk mixing prose and table rows averages both into one embedding: {string.Join(" || ", mixed)}");
    }

    [Fact]
    public void ATableTooLongForOneChunk_RepeatsItsHeaderInEveryPiece()
    {
        var text = "Intro prose.\n\n" + TableWith(40);

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 400);

        var withRows = chunks.Where(chunk => LinesOf(chunk).Any(IsTableLine)).ToList();
        withRows.Count.ShouldBeGreaterThan(1, "the table must actually be split for this to prove anything");
        foreach (var chunk in withRows)
        {
            var tableLines = LinesOf(chunk).Where(IsTableLine).ToList();
            tableLines[0].ShouldBe(Header.TrimEnd('\n'), "every table chunk must open with the header row");
            tableLines[1].ShouldBe(Separator.TrimEnd('\n'), "the separator must follow the header");
            tableLines.Count.ShouldBeGreaterThan(2, "a chunk of header and separator alone carries no content");
        }
    }

    [Fact]
    public void EveryTableChunk_StaysWithinTheTokenBudget()
    {
        const int maxTokens = 400;
        var chunks = new MarkdownChunker(CharCount).Chunk("Intro.\n\n" + TableWith(40), maxTokens);

        chunks.ShouldAllBe(chunk => chunk.Length <= maxTokens);
    }

    [Fact]
    public void EveryBodyRow_SurvivesTheSplitExactlyOnce()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("Intro.\n\n" + TableWith(40), 400);

        var rows = chunks.SelectMany(LinesOf)
            .Where(line => IsTableLine(line) && !IsSeparator(line) && line != Header.TrimEnd('\n'))
            .ToList();
        rows.Count.ShouldBe(40);
        rows.Distinct(StringComparer.Ordinal).Count().ShouldBe(40);
    }

    /// <summary>
    ///     ADR-0077's appendix: an early blast-radius figure counted any line with two or more pipes
    ///     and so caught shell pipelines, overstating table content 2.4x. A pipe is not a table.
    /// </summary>
    [Fact]
    public void APipeLineWithoutASeparatorRow_IsNotATable()
    {
        var text = "Run this:\ncat file | grep foo | wc -l\nand read the count.\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 4000);

        chunks.Count.ShouldBe(1, "a shell pipeline is prose and must not be split out as a table");
        chunks[0].ShouldBe(text);
    }

    [Fact]
    public void PipeLinesInsideAFence_AreNotATable()
    {
        var text = "Before.\n\n```sh\n| Bucket | What |\n|---|---|\n| a | b |\n```\n\nAfter.\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 4000);

        chunks.Count.ShouldBe(1, "a table drawn inside a code fence is fence content, not a table region");
    }

    [Fact]
    public void ATableEndingTheDocument_StillGetsItsOwnChunk()
    {
        var text = "Closing prose paragraph here.\n\n" + TableWith(2);

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 4000);

        chunks.ShouldContain(chunk => LinesOf(chunk).All(IsTableLine));
        chunks.ShouldContain(chunk => LinesOf(chunk).All(line => !IsTableLine(line)));
    }

    /// <summary>A table chunk must not pick up prose through the overlay either — the overlay is
    /// prose from the previous chunk, and prepending it re-creates the mixed chunk.</summary>
    [Fact]
    public void TheOverlay_DoesNotLeakProseIntoATableChunk()
    {
        var text = "Prose one.\nProse two.\nProse three.\n\n" + TableWith(3);

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 300, 60);

        var mixed = chunks.Where(chunk =>
            LinesOf(chunk).Any(IsTableLine) && LinesOf(chunk).Any(line => !IsTableLine(line))).ToList();
        mixed.ShouldBeEmpty($"overlay leaked prose into a table chunk: {string.Join(" || ", mixed)}");
    }
}
