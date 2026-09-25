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

    /// <summary>
    ///     Owner ruling 2026-09-25: the serve backend names its own version and install directory
    ///     once at startup, so it can be matched against InstallWatchdog's shutdown line (ADR-0116).
    ///     Extends the existing "serve listening" line (601) rather than adding a second one, and
    ///     moves it from Debug to Information — the version is meant to be seen, not opted into.
    /// </summary>
    [Fact]
    public void ServeListening_LogsInformation_WithVersionPortAndInstallDirectory()
    {
        var logger = new FakeLogger();

        NodeRunner.Log.ServeListening(logger, "1.51.3", 4242, "/fake/install/dir", "http://127.0.0.1:4242/mcp");

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Information);
        record.Id.Id.ShouldBe(601);
        record.Message.ShouldContain("1.51.3");
        record.Message.ShouldContain("4242");
        record.Message.ShouldContain("/fake/install/dir");
        record.Message.ShouldContain("http://127.0.0.1:4242/mcp");
    }
}
