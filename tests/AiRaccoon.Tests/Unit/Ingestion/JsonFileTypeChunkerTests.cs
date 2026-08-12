using AiRaccoon.Infrastructure.Ingestion;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class JsonFileTypeChunkerTests
{
    [Fact]
    public void Chunk_ValidJsonObject_ReturnsFormattedJsonChunks()
    {
        var chunker = new JsonFileTypeChunker();
        var json = """
        {
          "name": "ai-raccoon",
          "version": "1.7.0",
          "description": "C# .NET 10 MCP server exposing agent memory management"
        }
        """;

        var chunks = chunker.Chunk(json, maxTokens: 256, overlayTokens: 0);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk)));
    }

    [Fact]
    public void ExtractSchemaSummary_ExtractsNodeSchema()
    {
        var json = """
        {
          "name": "ai-raccoon",
          "version": "1.7.0",
          "enabled": true,
          "tags": ["memory", "mcp"]
        }
        """;

        var schema = JsonFileTypeChunker.ExtractSchemaSummary(json);

        Assert.Contains("\"name\": string", schema);
        Assert.Contains("\"version\": string", schema);
        Assert.Contains("\"enabled\": boolean", schema);
        Assert.Contains("\"tags\": [string]", schema);
    }

    [Fact]
    public void Chunk_LargeJsonObject_SplitsIntoMultipleTokenBoundedChunks()
    {
        var chunker = new JsonFileTypeChunker();
        var properties = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            properties.Add($"\"key_{i}\": \"This is value number {i} with a detailed description to exceed token budget\"");
        }
        var json = "{\n" + string.Join(",\n", properties) + "\n}";

        var chunks = chunker.Chunk(json, maxTokens: 60, overlayTokens: 0);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }

    [Fact]
    public void Chunk_MalformedJson_FallsBackToLineChunkingWithoutThrowing()
    {
        var chunker = new JsonFileTypeChunker();
        var invalidJson = "{\n  \"name\": \"ai-raccoon\",\n  \"unclosed_string\": \"oops\n";

        var chunks = chunker.Chunk(invalidJson, maxTokens: 256, overlayTokens: 0);

        Assert.NotEmpty(chunks);
        Assert.Contains("ai-raccoon", chunks[0]);
    }

    [Fact]
    public void Chunk_EmptyOrWhitespace_ReturnsEmptyOrSingleChunk()
    {
        var chunker = new JsonFileTypeChunker();
        var emptyChunks = chunker.Chunk("", maxTokens: 256);
        Assert.Empty(emptyChunks);

        var emptyObjChunks = chunker.Chunk("{}", maxTokens: 256);
        Assert.Single(emptyObjChunks);
        Assert.Equal("{}", emptyObjChunks[0]);
    }
}
