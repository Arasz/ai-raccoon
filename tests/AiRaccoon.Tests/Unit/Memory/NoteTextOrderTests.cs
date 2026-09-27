using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoteTextOrderTests
{
    private static readonly string Note = string.Join("\n\n", Enumerable.Range(0, 30).Select(i =>
        $"Item {i:D2} records that the committee approved the tombola budget line {i * 7:D3} after a vote."));

    private static readonly string NotePath = $"{ContentHash.OfValue(Note)}.md";

    /// <summary>The note as memory_write stores it: chunked with an overlay, so neighbouring rows repeat text.</summary>
    private static IReadOnlyList<string> Chunks()
    {
        var chunks = TestData.RealMarkdownChunker().Chunk(Note, 96, 24);
        chunks.Count.ShouldBeGreaterThan(3, "premise: the note spans several rows");
        chunks.Sum(chunk => chunk.Length).ShouldBeGreaterThan(Note.Length, "premise: neighbouring rows overlap");
        return chunks;
    }

    private static List<NoteRow> Rows(IReadOnlyList<string> chunks, IReadOnlyList<int> idOrder, IReadOnlyList<int>? positionOrder = null) =>
    [
        .. chunks.Select((value, i) => new NoteRow(
            Id: 100 + idOrder.ToList().IndexOf(i),
            Value: value,
            ChunkIndex: positionOrder is null ? -1 : positionOrder.ToList().IndexOf(i)))
    ];

    private static List<string> Values(IReadOnlyList<NoteRow>? rows) => rows.ShouldNotBeNull().Select(row => row.Value).ToList();

    [Fact]
    public void Find_FirstChunkWrittenLast_ReturnsTheTextOrder()
    {
        var chunks = Chunks();
        var idOrder = Enumerable.Range(1, chunks.Count - 1).Append(0).ToList();

        Values(NoteTextOrder.Find(NotePath, Rows(chunks, idOrder))).ShouldBe(chunks);
    }

    [Fact]
    public void Find_ChunksWrittenInTextOrder_ReturnsTheTextOrder()
    {
        var chunks = Chunks();

        Values(NoteTextOrder.Find(NotePath, Rows(chunks, [.. Enumerable.Range(0, chunks.Count)]))).ShouldBe(chunks);
    }

    /// <summary>A repair that re-chunked part of a note inserts new rows with new ids; only the positions still
    /// carry the order, rotated so the opening sits last.</summary>
    [Fact]
    public void Find_IdsScrambledButPositionsRotated_ReturnsTheTextOrder()
    {
        var chunks = Chunks();
        var idOrder = new[] { 2, 0, 3, 1 }.Concat(Enumerable.Range(4, chunks.Count - 4)).ToList();
        var positionOrder = new[] { 2 }.Concat(Enumerable.Range(3, chunks.Count - 3)).Concat([0, 1]).ToList();

        Values(NoteTextOrder.Find(NotePath, Rows(chunks, idOrder, positionOrder))).ShouldBe(chunks);
    }

    /// <summary>memory_write names the path after the body as sent, but the chunker stores it with \n line endings,
    /// so a body written with \r\n (or a lone \r) joins back only once those endings are restored.</summary>
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Find_BodyWrittenWithOtherLineEndings_ReturnsTheTextOrder(string lineEnding)
    {
        var body = Note.Replace("\n", lineEnding, StringComparison.Ordinal);
        var chunks = TestData.RealMarkdownChunker().Chunk(body, 96, 24);
        chunks.ShouldAllBe(chunk => !chunk.Contains('\r'), "premise: the chunker stores \\n line endings");
        var idOrder = Enumerable.Range(1, chunks.Count - 1).Append(0).ToList();

        Values(NoteTextOrder.Find($"{ContentHash.OfValue(body)}.md", Rows(chunks, idOrder))).ShouldBe(chunks);
    }

    /// <summary>A note of one line per row, stored with its opening last.</summary>
    private static (string Path, List<NoteRow> Rows) LineNote(int rowCount)
    {
        var lines = Enumerable.Range(0, rowCount).Select(i => $"Line {i:D5} of a very long note.\n").ToList();
        var idOrder = Enumerable.Range(1, rowCount - 1).Append(0).ToList();
        return ($"{ContentHash.OfValue(string.Concat(lines))}.md", Rows(lines, idOrder));
    }

    [Fact]
    public void Find_NoteWithAsManyRowsAsTheCap_ReturnsTheTextOrder()
    {
        var (path, rows) = LineNote(NoteTextOrder.MaxRows);

        NoteTextOrder.Find(path, rows).ShouldNotBeNull().Count.ShouldBe(NoteTextOrder.MaxRows);
    }

    /// <summary>Past the cap a note is left as stored rather than searched: the cost grows with the row count.</summary>
    [Fact]
    public void Find_NoteWithMoreRowsThanTheCap_ReturnsNull()
    {
        var (path, rows) = LineNote(NoteTextOrder.MaxRows + 1);

        NoteTextOrder.Find(path, rows).ShouldBeNull();
    }

    /// <summary>Identical paragraphs let every line boundary pass for an overlay, so a search over the joins could
    /// branch without end; a note it cannot prove must still stop inside a fixed budget.</summary>
    [Fact]
    public void Find_LargeRepetitiveNoteItCannotProve_StopsWithinTheWorkBudget()
    {
        var rows = Enumerable.Range(0, 800)
            .Select(i => new NoteRow(i, "Same paragraph.\nSame paragraph.\nSame paragraph.\n", i))
            .ToList();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var order = NoteTextOrder.Find($"{ContentHash.OfValue("some other body")}.md", rows, out var work);

        order.ShouldBeNull();
        work.ShouldBeLessThanOrEqualTo(NoteTextOrder.MaxWork);
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Find_RowsThatDoNotJoinBackIntoTheNote_ReturnsNull()
    {
        var chunks = Chunks().ToList();
        chunks[1] = chunks[1].Replace("committee", "council", StringComparison.Ordinal);

        NoteTextOrder.Find(NotePath, Rows(chunks, [.. Enumerable.Range(0, chunks.Count)])).ShouldBeNull();
    }

    [Fact]
    public void Find_PathThatDoesNotNameTheBodyHash_ReturnsNull()
    {
        var chunks = Chunks();

        NoteTextOrder.Find("notes/minutes.md", Rows(chunks, [.. Enumerable.Range(0, chunks.Count)])).ShouldBeNull();
    }

    [Fact]
    public void Repositioned_ReusesTheRowsOwnPositionsInTextOrder()
    {
        List<NoteRow> inTextOrder = [new(12, "a", 7), new(10, "b", 3), new(11, "c", 5)];

        NoteTextOrder.Repositioned(inTextOrder).ShouldBe([(12L, 3L), (10L, 5L), (11L, 7L)]);
    }

    [Fact]
    public void Repositioned_AlreadyInTextOrder_MovesNothing()
    {
        NoteTextOrder.Repositioned([new(10, "a", 3), new(11, "b", 4)]).ShouldBeEmpty();
    }

    [Fact]
    public void Repositioned_AnUnknownPosition_MovesNothing()
    {
        NoteTextOrder.Repositioned([new(10, "a", 4), new(11, "b", -1)]).ShouldBeEmpty();
    }
}
