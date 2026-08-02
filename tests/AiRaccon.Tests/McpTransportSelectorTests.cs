using Shouldly;
using Xunit;

namespace AiRaccon.Tests;

public class McpTransportSelectorTests
{
    [Theory]
    [InlineData("http", true)]
    [InlineData("HTTP", true)]
    [InlineData("Http", true)]
    [InlineData("stdio", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UseHttp_ReturnsExpected(string? transport, bool expected)
    {
        McpTransportSelector.UseHttp(transport).ShouldBe(expected);
    }
}
