using AiRaccoon.Setup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class LoggerMessageTests
{
    [Fact]
    public void HttpsTransportNotSupported_LogsWarning_WithEventId()
    {
        var logger = new FakeLogger();

        McpServerSetup.Log.HttpsTransportNotSupported(logger);

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Id.Id.ShouldBe(1);
        record.Message.ShouldContain("https transport is not supported");
    }
}
