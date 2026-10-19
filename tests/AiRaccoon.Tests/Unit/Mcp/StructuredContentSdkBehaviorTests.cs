using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class StructuredContentSdkBehaviorTests
{
    [Fact]
    public async Task AttributedPoco_UsesStructuredContent_AndKeepsJsonTextFallback()
    {
        var tool = McpServerTool.Create(
            (Func<StructuredSearchResult>)StructuredSearchProbe.Search,
            new McpServerToolCreateOptions());
        var server = Substitute.For<McpServer>();
        server.NegotiatedProtocolVersion.Returns("2025-11-25");
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams
            {
                Name = "structured_search",
                Arguments = new Dictionary<string, JsonElement>()
            });

        tool.ProtocolTool.OutputSchema.ShouldNotBeNull();
        var schema = tool.ProtocolTool.OutputSchema!.Value;
        schema.GetProperty("properties").GetProperty("status").GetProperty("type")
            .GetString().ShouldBe("string");
        schema.GetProperty("properties").GetProperty("count").GetProperty("type")
            .GetString().ShouldBe("integer");
        var result = await tool.InvokeAsync(context, CancellationToken.None);

        result.StructuredContent.ShouldNotBeNull();
        result.StructuredContent!.Value.GetProperty("status").GetString().ShouldBe("ok");
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.ShouldBe(result.StructuredContent.Value.GetRawText());
    }

    public sealed record StructuredSearchResult(string Status, int Count);

    public static class StructuredSearchProbe
    {
        [McpServerTool(Name = "structured_search", UseStructuredContent = true)]
        [Description("Returns a sample structured search result.")]
        public static StructuredSearchResult Search() => new("ok", 1);
    }
}
