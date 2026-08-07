using System.Text.Json;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Serve;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Characterizes the wire-visible outputSchema the MCP SDK emits for memory_write. No
///     assertion on outputSchema existed before this — it is the gate for issue #118 item 1
///     (the ApiEnvelope/ResponseMeta rename), which must not silently change the wire shape.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ToolOutputSchemaTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("mcp-output-schema-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public void MemoryWrite_OutputSchema_MatchesTheCharacterizedShape()
    {
        var host = McpServerSetup.CreateServerHost(
            new ServerConfig(7721, McpTransport.Stdio, TestData.CreateInfrastructureOptions(_dataRoot), default));

        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var tool = (options.ToolCollection ?? throw new InvalidOperationException("ToolCollection not configured"))
            .Single(t => t.ProtocolTool.Name == "memory_write");

        var schema = JsonSerializer.Serialize(tool.ProtocolTool.OutputSchema);

        schema.ShouldBe(ExpectedOutputSchema);
    }

    // Observed on unmodified main (b177df5): no tool opts into UseStructuredContent, so the
    // SDK never populates OutputSchema — this is the wire truth today, not an omission here.
    private const string ExpectedOutputSchema = "null";
}
