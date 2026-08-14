using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class FileTypeMatcherTests
{
    private sealed class FakeFileTypeHandler(string name, string[] extensions) : IFileTypeHandler
    {
        public string Name { get; } = name;
        public IReadOnlySet<string> Extensions { get; } = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public IChunker Chunker { get; } = new FakeChunker();
    }

    private sealed class FakeChunker : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null) => [text];
    }

    [Fact]
    public void TryGetHandler_ReturnsHandler_WhenExtensionMatches()
    {
        var jsonHandler = new FakeFileTypeHandler("Json", [".json"]);
        var mdHandler = new FakeFileTypeHandler("Markdown", [".md", ".markdown", ".txt"]);
        var matcher = new FileTypeMatcher([jsonHandler, mdHandler]);

        Assert.True(matcher.TryGetHandler("config.json", out var resultJson));
        Assert.Equal("Json", resultJson.Name);

        Assert.True(matcher.TryGetHandler("README.MD", out var resultMd));
        Assert.Equal("Markdown", resultMd.Name);
    }

    [Fact]
    public void TryGetHandler_ReturnsFalse_WhenExtensionNotSupported()
    {
        var jsonHandler = new FakeFileTypeHandler("Json", [".json"]);
        var matcher = new FileTypeMatcher([jsonHandler]);

        Assert.False(matcher.TryGetHandler("image.png", out var handler));
        Assert.Null(handler);
        Assert.False(matcher.IsSupported("image.png"));
    }

    [Fact]
    public void Constructor_ThrowsInvalidOperationException_WhenDuplicateExtensionRegistered()
    {
        var handler1 = new FakeFileTypeHandler("Handler1", [".json"]);
        var handler2 = new FakeFileTypeHandler("Handler2", [".JSON"]);

        var ex = Assert.Throws<InvalidOperationException>(() => new FileTypeMatcher([handler1, handler2]));
        Assert.Contains(".json", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
