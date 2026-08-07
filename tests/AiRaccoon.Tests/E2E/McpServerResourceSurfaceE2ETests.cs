using AiRaccoon.Tests.Unit.Embedding;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     SEP-2640 skill surface over the real HTTP MCP server: resources/list must surface the
///     skill index and the memory skill, and both must be readable via resources/read.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public class McpServerResourceSurfaceE2ETests : IAsyncLifetime
{
    private const string IndexUri = "skill://index.json";
    private const string MemorySkillUri = "skill://ai-raccoon-memory/SKILL.md";

    private McpClient _client = null!;
    private McpServerFactory _factory = null!;
    private FakeEmbeddingEndpoint _openAi = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory();
        _client = await _factory.CreateClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _factory.DisposeAsync();
        await _openAi.DisposeAsync();
    }

    [Fact]
    public async Task ResourcesList_SurfacesSkillIndexAndMemorySkill()
    {
        var resources = await _client.ListResourcesAsync((RequestOptions?)null, TestContext.Current.CancellationToken);

        var index = resources.Single(r => r.Uri == IndexUri);
        index.MimeType.ShouldBe("application/json");
        index.Name.ShouldNotBeNullOrWhiteSpace();

        var skill = resources.Single(r => r.Uri == MemorySkillUri);
        skill.MimeType.ShouldBe("text/markdown");
        skill.Name.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReadResource_SkillIndex_ReturnsDiscoveryDocument()
    {
        var result = await _client.ReadResourceAsync(new Uri(IndexUri), (RequestOptions?)null, TestContext.Current.CancellationToken);

        var text = result.Contents.OfType<TextResourceContents>().ShouldHaveSingleItem().Text;
        text.ShouldContain("\"$schema\"");
        text.ShouldContain("ai-raccoon-memory");
        text.ShouldContain("skill://ai-raccoon-memory/SKILL.md");
    }

    [Fact]
    public async Task ReadResource_MemorySkill_ReturnsSkillBody()
    {
        var result = await _client.ReadResourceAsync(new Uri(MemorySkillUri), (RequestOptions?)null, TestContext.Current.CancellationToken);

        var text = result.Contents.OfType<TextResourceContents>().ShouldHaveSingleItem().Text;
        text.ShouldStartWith("---");
        text.ShouldContain("name: ai-raccoon-memory");
        text.ShouldContain("## 2. Search-first workflow");
    }
}
