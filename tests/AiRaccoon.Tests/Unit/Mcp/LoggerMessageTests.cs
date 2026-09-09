using AiRaccoon.Hosting.Node;
using AiRaccoon.Setup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class LoggerMessageTests
{
    [Fact]
    public void IgnoringTransport_LogsWarning_WithEventId()
    {
        var logger = new FakeLogger();

        NodeRunner.Log.IgnoringTransport(logger, McpTransport.Proxy);

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Id.Id.ShouldBe(602);
        record.Message.ShouldContain("serve always uses http");
        record.Message.ShouldContain("Proxy");
    }
}
