using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests;

public class McpServerSetupTests
{
    [Theory]
    [InlineData("http", McpTransport.Http)]
    [InlineData("HTTP", McpTransport.Http)]
    [InlineData("Http", McpTransport.Http)]
    [InlineData("stdio", McpTransport.Stdio)]
    [InlineData("", McpTransport.Stdio)]
    [InlineData(null, McpTransport.Stdio)]
    public void SelectTransports_ResolvesEnvironmentValue(string? transport, McpTransport expected)
    {
        McpServerSetup.SelectTransports(transport).ShouldBe([expected]);
    }
}
