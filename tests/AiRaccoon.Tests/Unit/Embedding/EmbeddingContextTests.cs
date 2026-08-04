using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingContextTests
{
    [Fact]
    public void ContextTokensFor_LocalEngine_IsTheBundledModelsWindow() =>
        EmbeddingService.ContextTokensFor("local", null)
            .ShouldBe(EmbeddingService.BundledModelContextTokens);

    [Fact]
    public void ContextTokensFor_OpenAiEngine_IsTheDocumentedEmbeddingWindow() =>
        EmbeddingService.ContextTokensFor("openai", "text-embedding-3-small")
            .ShouldBe(EmbeddingService.OpenAiEmbeddingContextTokens);

    [Fact]
    public void ContextTokensFor_UnknownProvider_DefaultsConservatively() =>
        EmbeddingService.ContextTokensFor("mystery", null)
            .ShouldBe(EmbeddingService.BundledModelContextTokens);
}
