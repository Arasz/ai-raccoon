using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Chunking;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class NotebookChunkerTests
{
    private const string Image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly NotebookChunker Chunker = new(TestData.RealMarkdownChunker());

    private static string Notebook(string cells, string metadata = """{"kernelspec":{"language":"python"}}""") =>
        $$"""{"metadata":{{metadata}},"nbformat":4,"cells":[{{cells}}]}""";

    private static string Joined(IEnumerable<TextChunk> chunks) => string.Join("\n", chunks.Select(c => c.Text));

    private const string MixedCells = $$$"""
        {"cell_type":"markdown","source":["Intro line\n","Explains the notebook."]},
        {"cell_type":"code","source":"print('hello cell')","outputs":[
          {"output_type":"stream","name":"stdout","text":["STREAM-OUTPUT-MARKER\n"]},
          {"output_type":"display_data","data":{"image/png":"{{{Image}}}","text/plain":["<Figure PLAIN-OUTPUT>"]}}]}
        """;

    [Fact]
    public void ChunkWithHeadings_KeepsCellSources()
    {
        var chunks = Chunker.ChunkWithHeadings(Notebook(MixedCells), 512);

        var text = Joined(chunks);
        Assert.Contains("Explains the notebook.", text);
        Assert.Contains("print('hello cell')", text);
        Assert.Contains("```python", text);
    }

    [Fact]
    public void ChunkWithHeadings_DropsOutputsAndImages()
    {
        var text = Joined(Chunker.ChunkWithHeadings(Notebook(MixedCells), 512));

        Assert.DoesNotContain("STREAM-OUTPUT-MARKER", text);
        Assert.DoesNotContain("PLAIN-OUTPUT", text);
        Assert.DoesNotContain(Image[..24], text);
    }

    [Fact]
    public void ChunkWithHeadings_NamesEachCellInSections()
    {
        var sections = Chunker.ChunkWithHeadings(Notebook(MixedCells), 512).SelectMany(c => c.Sections).ToList();

        Assert.Contains("cell 1 (markdown)", sections);
        Assert.Contains("cell 2 (code)", sections);
    }

    [Fact]
    public void ChunkWithHeadings_AcceptsStringAndArraySources()
    {
        const string cells = """
            {"cell_type":"code","source":"x = 1\ny = 2"},
            {"cell_type":"code","source":["a = 1\n","b = 2"]}
            """;

        var text = Joined(Chunker.ChunkWithHeadings(Notebook(cells), 512));

        Assert.Contains("x = 1\ny = 2", text);
        Assert.Contains("a = 1\nb = 2", text);
    }

    [Fact]
    public void ChunkWithHeadings_SkipsEmptyAndRawCells_ButKeepsCellNumbering()
    {
        const string cells = """
            {"cell_type":"raw","source":"RAW-CONTENT"},
            {"cell_type":"code","source":[]},
            {"cell_type":"markdown","source":"kept"}
            """;

        var chunks = Chunker.ChunkWithHeadings(Notebook(cells), 512);

        Assert.DoesNotContain("RAW-CONTENT", Joined(chunks));
        Assert.Equal(["cell 3 (markdown)"], chunks.SelectMany(c => c.Sections).Distinct());
    }

    [Fact]
    public void ChunkWithHeadings_UsesLanguageInfoWhenNoKernelspec()
    {
        var notebook = Notebook("""{"cell_type":"code","source":"1+1"}""", """{"language_info":{"name":"julia"}}""");

        Assert.Contains("```julia", Joined(Chunker.ChunkWithHeadings(notebook, 512)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("""{"metadata":{}}""")]
    [InlineData("""{"cells":"nope"}""")]
    public void ChunkWithHeadings_UnreadableNotebook_YieldsNoChunks(string input)
    {
        Assert.Empty(Chunker.ChunkWithHeadings(input, 512));
        Assert.Empty(Chunker.Chunk(input, 512));
    }

    [Fact]
    public void ChunkWithHeadings_LongCell_IsSplitUnderItsCellHeading()
    {
        var body = string.Join("\\n", Enumerable.Range(0, 400).Select(i => $"value_{i} = compute({i})"));
        var notebook = Notebook($$"""{"cell_type":"code","source":"{{body}}"}""");

        var chunks = Chunker.ChunkWithHeadings(notebook, 120);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.Contains("cell 1 (code)", c.HeadingPath));
        Assert.All(chunks, c => Assert.Contains("value_", c.Text));
    }

    [Fact]
    public void ChunkWithHeadings_CodeCellWithFenceLine_StaysOneCodeChunk()
    {
        var notebook = Notebook("""{"cell_type":"code","source":"s = \"\"\"\n```\n# not a heading\n```` x\n\"\"\""}""");

        var chunks = Chunker.ChunkWithHeadings(notebook, 512);

        Assert.All(chunks, c => Assert.Contains("cell 1 (code)", c.HeadingPath));
        Assert.DoesNotContain(chunks.SelectMany(c => c.Sections), s => s.Contains("not a heading"));
        Assert.Contains("# not a heading", Joined(chunks));
    }

    [Fact]
    public void ChunkWithHeadings_MarkdownCellWithUnclosedFence_DoesNotSwallowTheNextCell()
    {
        var notebook = Notebook("""{"cell_type":"markdown","source":"```python\nx = 1"},{"cell_type":"code","source":"y = 2"}""");

        var chunks = Chunker.ChunkWithHeadings(notebook, 512);

        Assert.Contains(chunks, c => c.Sections.Any(s => s.Contains("cell 2 (code)")) && c.Text.Contains("y = 2"));
    }

    [Fact]
    public void ChunkWithHeadings_MarkdownCellHeadings_NestUnderTheCellSection()
    {
        var notebook = Notebook("""{"cell_type":"markdown","source":["# Title\n","text\n","## Sub\n","more"]}""");

        var chunks = Chunker.ChunkWithHeadings(notebook, 512);

        Assert.All(chunks, c => Assert.Contains("cell 1 (markdown)", c.HeadingPath));
        Assert.Contains("### Title", Joined(chunks));
        Assert.Contains("#### Sub", Joined(chunks));
    }
}
