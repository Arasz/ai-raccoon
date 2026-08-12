using System.Text.Json;
using AiRaccoon.Setup.Cli.Render;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     Golden shapes for the --mcp-entry renderer: single-line JSON documents, hermes
///     (url + headers entry) and claude (type+url+headers entry), and 'all' printing both in
///     order. Both entries carry the token header as a `${VAR}` placeholder, never a live secret
///     (docs/plans/2026-08-09-http-token-clients.md D4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class McpEntryRendererTests
{
    [Fact]
    public void RenderHermes_MatchesGoldenJson_OrderInsensitive()
    {
        var json = McpEntryRenderer.RenderHermes(7721);

        JsonDeepEqual(json, """
                            {"ai-raccoon":{"url":"http://127.0.0.1:7721/mcp","headers":{"X-AiRaccoon-Token":"${AIRACCOON_MCP_TOKEN}"}}}
                            """).ShouldBeTrue();
        json.ShouldNotContain('\n');
        json.TrimEnd().ShouldBe(json);
    }

    [Fact]
    public void RenderClaude_MatchesGoldenJson_OrderInsensitive()
    {
        var json = McpEntryRenderer.RenderClaude(7721);

        JsonDeepEqual(json, """
                            {"mcpServers":{"ai-raccoon":{"type":"http","url":"http://127.0.0.1:7721/mcp","headers":{"X-AiRaccoon-Token":"${AIRACCOON_MCP_TOKEN}"}}}}
                            """).ShouldBeTrue();
        json.ShouldNotContain('\n');
        json.TrimEnd().ShouldBe(json);
    }

    [Fact]
    public void RenderAll_PrintsBothDocumentsInOrder_WithBlankLineBetween()
    {
        McpEntryRenderer.RenderAll(9000).ShouldBe(
            $"{McpEntryRenderer.RenderHermes(9000)}\n{McpEntryRenderer.RenderClaude(9000)}");
    }

    /// <summary>
    ///     R7: criterion 1/2 already deep-equal the whole document, so a live token cannot appear
    ///     without failing them first — "no 43-char Base64Url run" was unfalsifiable. This is the
    ///     check that can actually fail: neither renderer takes a token as input at all.
    /// </summary>
    [Theory]
    [InlineData(nameof(McpEntryRenderer.RenderHermes))]
    [InlineData(nameof(McpEntryRenderer.RenderClaude))]
    public void Renderer_TakesNoTokenParameter(string methodName)
    {
        var method = typeof(McpEntryRenderer).GetMethod(methodName);

        method.ShouldNotBeNull();
        method.GetParameters().Select(p => p.Name).ShouldBe(["port"]);
    }

    private static bool JsonDeepEqual(string actual, string expected)
    {
        using var actualDocument = JsonDocument.Parse(actual);
        using var expectedDocument = JsonDocument.Parse(expected);
        return NodesEqual(actualDocument.RootElement, expectedDocument.RootElement);
    }

    private static bool NodesEqual(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind)
        {
            return false;
        }

        switch (actual.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var expectedProperties = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                foreach (var property in actual.EnumerateObject())
                {
                    if (!expectedProperties.TryGetValue(property.Name, out var expectedValue) ||
                        !NodesEqual(property.Value, expectedValue))
                    {
                        return false;
                    }
                }

                return actual.EnumerateObject().Count() == expected.EnumerateObject().Count();
            }
            case JsonValueKind.Array:
            {
                var actualItems = actual.EnumerateArray().ToList();
                var expectedItems = expected.EnumerateArray().ToList();
                if (actualItems.Count != expectedItems.Count)
                {
                    return false;
                }

                for (var i = 0; i < actualItems.Count; i++)
                {
                    if (!NodesEqual(actualItems[i], expectedItems[i]))
                    {
                        return false;
                    }
                }

                return true;
            }
            default:
                return actual.GetRawText() == expected.GetRawText();
        }
    }
}
