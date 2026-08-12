using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Ingestion;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ChunkerComparisonTests
{
    private readonly ITestOutputHelper _output;

    public ChunkerComparisonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Compare_MarkdownAndJson_ChunkingResults()
    {
        var mdHandler = new MarkdownFileTypeHandler();
        var jsonHandler = new JsonFileTypeHandler();

        var markdownContent = """
        # AiRaccoon Configuration

        ## System Settings
        app: ai-raccoon
        version: 1.8.0

        ## Features
        json_chunking: true
        extensible_handlers: true
        """;

        var jsonContent = """
        {
          "System Settings": {
            "app": "ai-raccoon",
            "version": "1.8.0"
          },
          "Features": {
            "json_chunking": true,
            "extensible_handlers": true
          }
        }
        """;

        // 1) Test with full maxTokens (single chunk for small doc)
        var mdChunksFull = mdHandler.Chunker.Chunk(markdownContent, maxTokens: 256);
        var jsonChunksFull = jsonHandler.Chunker.Chunk(jsonContent, maxTokens: 256);

        _output.WriteLine("=== MARKDOWN CHUNKS (maxTokens=256) ===");
        for (var i = 0; i < mdChunksFull.Count; i++)
        {
            _output.WriteLine($"--- Chunk {i + 1} ---");
            _output.WriteLine(mdChunksFull[i]);
        }

        _output.WriteLine("\n=== JSON CHUNKS (maxTokens=256) ===");
        for (var i = 0; i < jsonChunksFull.Count; i++)
        {
            _output.WriteLine($"--- Chunk {i + 1} ---");
            _output.WriteLine(jsonChunksFull[i]);
        }

        // 2) Test with tight maxTokens (forces structural splitting)
        var mdChunksTight = mdHandler.Chunker.Chunk(markdownContent, maxTokens: 15);
        var jsonChunksTight = jsonHandler.Chunker.Chunk(jsonContent, maxTokens: 20);

        _output.WriteLine("\n=== MARKDOWN CHUNKS (Tight maxTokens=15) ===");
        for (var i = 0; i < mdChunksTight.Count; i++)
        {
            _output.WriteLine($"--- Chunk {i + 1} ---");
            _output.WriteLine(mdChunksTight[i]);
        }

        _output.WriteLine("\n=== JSON CHUNKS (Tight maxTokens=20) ===");
        for (var i = 0; i < jsonChunksTight.Count; i++)
        {
            _output.WriteLine($"--- Chunk {i + 1} ---");
            _output.WriteLine(jsonChunksTight[i]);
        }

        Assert.NotEmpty(mdChunksFull);
        Assert.NotEmpty(jsonChunksFull);
        Assert.NotEmpty(mdChunksTight);
        Assert.NotEmpty(jsonChunksTight);
    }
}
