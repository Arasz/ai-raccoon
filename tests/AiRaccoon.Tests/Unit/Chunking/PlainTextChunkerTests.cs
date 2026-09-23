using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     A .txt file has no markdown syntax: a '#' line is text (a comment, usually) and a ``` line is
///     text. The plain-text chunker packs lines exactly as the markdown one does, minus those two rules.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class PlainTextChunkerTests
{
    private static int CharCount(string text) => text.Length;

    [Fact]
    public void ChunkWithHeadings_HashLines_AreTextNotSections()
    {
        var text = "# Install\npython3 -m pip install -r requirements.txt\n\n## Dev tools\npytest\nruff\nmypy\n";

        var chunks = new PlainTextChunker(CharCount).ChunkWithHeadings(text, 40);

        chunks.Count.ShouldBeGreaterThan(1, "the fixture is sized to span several chunks");
        chunks.ShouldAllBe(chunk => chunk.Sections.Count == 0 && chunk.HeadingPath == "");
    }

    [Theory]
    [InlineData("```\n")]
    [InlineData("~~~\n")]
    public void Chunk_FenceMarkerLines_AreKeptVerbatim(string marker)
    {
        var text = "intro line\n" + marker + string.Concat(Enumerable.Range(0, 30).Select(i => $"line {i} of a log\n"));

        var chunks = new PlainTextChunker(CharCount).Chunk(text, 60);

        string.Concat(chunks).ShouldBe(text, "no fence opener or closer may be added to plain text");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(0)]
    public void Chunk_TextWithoutMarkdownSyntax_MatchesTheMarkdownChunker(int overlayTokens)
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 12).Select(i =>
            $"Paragraph {i}: the harbour log records tides, fog and the ferry timetable for the week.\n"
            + new string('x', i * 9)));

        var plain = new PlainTextChunker(CharCount).Chunk(text, 120, overlayTokens);
        var markdown = new MarkdownChunker(CharCount).Chunk(text, 120, overlayTokens);

        plain.ShouldBe(markdown, "unchanged chunks keep their hashes, so existing rows need no re-ingest");
    }
}
