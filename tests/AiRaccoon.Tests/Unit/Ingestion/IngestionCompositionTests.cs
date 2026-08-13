using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Ingestion;
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

    private static IReadOnlyList<IFileTypeHandler> ResolveFileTypeHandlers()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(TestData.CreateTempRoot()));
        return services.BuildServiceProvider().GetServices<IFileTypeHandler>().ToList();
    }
}
