using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class McpServerSetupTests
{
    [Theory]
    [InlineData("http", McpTransport.Http)]
    [InlineData("HTTP", McpTransport.Http)]
    [InlineData("Http", McpTransport.Http)]
    [InlineData("stdio", McpTransport.Stdio)]
    [InlineData("proxy", McpTransport.Proxy)]
    public void SelectTransports_ResolvesTheNamedTransport(string transport, McpTransport expected) =>
        McpServerSetup.SelectTransports(transport).ShouldBe([expected]);

    /// <summary>One default, not two: an unparseable value lands on the launch default itself.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void SelectTransports_UnparseableValue_FallsBackToTheLaunchDefault(string? transport) =>
        McpServerSetup.SelectTransports(transport).ShouldBe([DefaultOptions.Transport]);
}
