using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Building the DI container, and resolving the services that depend on
///     <see cref="LocalTokenizer" />, must never build the underlying BertTokenizer — a broken
///     bundled install (deleted vocab.txt) must keep failing at first *use*, not at container-build
///     or resolution time (mirrors RegisterCoreMemoryServices DI-wiring tests such as
///     NoiseLearningRegistrationTests).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LocalTokenizerRegistrationTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-local-tokenizer-di");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(_dataRoot));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RegisterCoreMemoryServices_ResolvingEmbeddingService_DoesNotBuildTheLocalTokenizer()
    {
        using var provider = BuildProvider();

        _ = provider.GetRequiredService<IEmbeddingService>();
        var localTokenizer = provider.GetRequiredService<LocalTokenizer>();

        localTokenizer.IsTokenizerBuilt.ShouldBeFalse(
            "resolving EmbeddingService (and the LocalTokenizer it depends on) must not itself parse the vocab");
    }

    [Fact]
    public void RegisterCoreMemoryServices_LocalTokenizer_IsRegisteredAsASingleton()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ILocalTokenizer>();
        var second = provider.GetRequiredService<ILocalTokenizer>();

        first.ShouldBeSameAs(second, "every consumer must share the same LocalTokenizer instance");
    }
}
