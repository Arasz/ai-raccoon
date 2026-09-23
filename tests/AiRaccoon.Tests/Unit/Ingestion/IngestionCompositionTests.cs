using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class IngestionCompositionTests
{
    [Fact]
    public void MarkdownAndJsonHandlers_GetDistinctChunkers()
    {
        var handlers = ResolveFileTypeHandlers();
        var markdown = handlers.Single(h => h.Name == "Markdown");
        var json = handlers.Single(h => h.Name == "Json");

        // The JSON handler owns the JSON chunker.
        Assert.IsType<JsonFileTypeChunker>(json.Chunker);

        // The Markdown handler must NOT be wired to the JSON chunker — its chunker is the
        // markdown/text splitter. (RED today: the second IChunker registration wins for both.)
        Assert.IsNotType<JsonFileTypeChunker>(markdown.Chunker);
        Assert.NotSame(markdown.Chunker, json.Chunker);
    }

    [Fact]
    public void TxtFiles_GoToThePlainTextHandler_AndMarkdownFilesDoNot()
    {
        var handlers = ResolveFileTypeHandlers();

        var txt = handlers.Single(h => h.Extensions.Contains(".txt"));
        Assert.Equal("PlainText", txt.Name);
        Assert.IsType<PlainTextChunker>(txt.Chunker);
        Assert.Equal("Markdown", handlers.Single(h => h.Extensions.Contains(".md")).Name);
    }

    private static IReadOnlyList<IFileTypeHandler> ResolveFileTypeHandlers()
    {
        var tempRoot = TestData.CreateTempRoot();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(tempRoot));
            return [.. services.BuildServiceProvider().GetServices<IFileTypeHandler>()];
        }
        finally
        {
            TestData.DeleteTempRoot(tempRoot);
        }
    }
}
