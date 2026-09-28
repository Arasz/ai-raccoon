using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     config-D P1a (plan §3 row 4): a note group's chunks-with-overlay reassemble — via
///     <see cref="NoteTextOrder" />'s backtracking overlay search, never a greedy longest-match strip —
///     into the byte-identical body its path names, proven by sha256(merged) == the path stem. A group
///     no join proves fails verification instead of corrupting anything.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ChunkReassemblyTests
{
    private static readonly string Note = string.Join("\n\n", Enumerable.Range(0, 30).Select(i =>
        $"Item {i:D2} records that the committee approved the tombola budget line {i * 7:D3} after a vote."));

    private static readonly string NotePath = $"{ContentHash.OfValue(Note)}.md";

    /// <summary>The note as memory_write stores it: chunked with an overlay, so neighbouring rows repeat text.</summary>
    private static List<NoteRow> ChunkedRows(string body, int maxTokens = 96, int overlayTokens = 24,
        IReadOnlyList<int>? idOrder = null)
    {
        var chunks = TestData.RealMarkdownChunker().Chunk(body, maxTokens, overlayTokens);
        chunks.Count.ShouldBeGreaterThan(3, "premise: the note spans several rows");
        chunks.Sum(chunk => chunk.Length).ShouldBeGreaterThan(body.Length, "premise: neighbouring rows overlap");
        idOrder ??= [.. Enumerable.Range(0, chunks.Count)];
        return [.. chunks.Select((value, i) => new NoteRow(100 + idOrder.ToList().IndexOf(i), value, -1))];
    }

    [Fact]
    public void TryMerge_ChunksWithOverlay_MergeByteIdenticallyToTheOriginalText()
    {
        // Opening written last: only NoteTextOrder's rotation candidates find the order at all.
        var rows = ChunkedRows(Note, idOrder: [.. Enumerable.Range(1, 29).Append(0)]);

        var merged = ChunkReassembly.TryMerge(NotePath, rows);

        merged.ShouldBe(Note, "the reassembly must be byte-identical to the note as written");
        ContentHash.OfValue(merged!).ShouldBe(Path.GetFileNameWithoutExtension(NotePath));
    }

    /// <summary>
    ///     A body written with \r\n is stored with \n row endings; the merge must restore the body as sent
    ///     (that is what hashes to the path stem), not hand back the stored rows' endings.
    /// </summary>
    [Fact]
    public void TryMerge_BodyWrittenWithCarriageReturns_MergesTheBodyAsSent()
    {
        var body = Note.Replace("\n", "\r\n", StringComparison.Ordinal);
        var path = $"{ContentHash.OfValue(body)}.md";
        var rows = ChunkedRows(body);

        var merged = ChunkReassembly.TryMerge(path, rows);

        merged.ShouldBe(body, "merged content must be byte-identical to the body as written");
    }

    /// <summary>
    ///     At this boundary the longest suffix-prefix match ("aa\naa\n") is LONGER than the real overlay
    ///     ("aa\n") and eats live text: a greedy matcher merges "aa\naa\nend\n" and misses the hash, while
    ///     backtracking over every whole-line overlay candidate finds the join that hashes to the stem
    ///     (review F1 mech 3 — the reason P1a wraps NoteTextOrder instead of a greedy matcher).
    /// </summary>
    [Fact]
    public void TryMerge_RepeatedBoundaryTheGreedyMatchWouldEat_StillMergesTheOriginalText()
    {
        const string body = "aa\naa\naa\nend\n";
        var path = $"{ContentHash.OfValue(body)}.md";
        List<NoteRow> rows = [new(2, "aa\naa\n", -1), new(5, "aa\naa\nend\n", -1)];

        var merged = ChunkReassembly.TryMerge(path, rows);

        merged.ShouldBe(body, "the real overlay is one line; only backtracking proves which repeat is the overlay");
    }

    [Fact]
    public void TryMerge_AnExactMerge_IsAccepted()
    {
        var merged = ChunkReassembly.TryMerge(NotePath, ChunkedRows(Note));

        merged.ShouldNotBeNull("an exact merge hashes to the path stem and must be accepted");
        ContentHash.OfValue(merged).ShouldBe(Path.GetFileNameWithoutExtension(NotePath));
    }

    [Fact]
    public void TryMerge_ATamperedRow_IsRejected()
    {
        var rows = ChunkedRows(Note);
        rows[1] = rows[1] with { Value = rows[1].Value.Replace("committee", "council", StringComparison.Ordinal) };

        ChunkReassembly.TryMerge(NotePath, rows).ShouldBeNull(
            "a tampered row cannot hash to the path stem; verification must reject it");
    }

    /// <summary>Rows that never formed this note stay unmerged — verification fails instead of inventing content.</summary>
    [Fact]
    public void TryMerge_RowsThatDoNotJoinBackIntoTheNote_FailVerificationInsteadOfCorrupting()
    {
        var rows = ChunkedRows(Note);

        ChunkReassembly.TryMerge($"{ContentHash.OfValue("some other body entirely")}.md", rows).ShouldBeNull(
            "no join of these rows names this path; the group must come back unproven, never guessed");
    }
}
