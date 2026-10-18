using System.Reflection;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     A tool call the client gave up on is already logged by ToolRefusals (914/915). The SDK's own
///     "request handler failed" record for it carries only the stack trace, and no return value can
///     prevent it: the SDK's response send throws on the dead request token inside the same try.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CancelledRequestLoggingTests
{
    private const string SdkCategory = "ModelContextProtocol.Server.McpServer";
    private static readonly EventId SdkEvent = new(975074943, "LogRequestHandlerException");

    [Fact]
    public void SdkHandlerFailure_WithCancellation_IsDropped()
    {
        var (factory, collector) = HostFactory();

        factory.CreateLogger(SdkCategory).Log(LogLevel.Warning, SdkEvent, "failed", new TaskCanceledException(), (s, _) => s);

        collector.Count.ShouldBe(0);
    }

    [Fact]
    public void SdkHandlerFailure_WithAnyOtherException_IsKept()
    {
        var (factory, collector) = HostFactory();

        factory.CreateLogger(SdkCategory).Log(LogLevel.Warning, SdkEvent, "failed", new InvalidOperationException(), (s, _) => s);

        collector.Count.ShouldBe(1);
    }

    [Fact]
    public void SameEventName_OutsideTheSdk_IsKept()
    {
        var (factory, collector) = HostFactory();

        factory.CreateLogger("AiRaccoon.Anything").Log(LogLevel.Warning, SdkEvent, "failed", new TaskCanceledException(), (s, _) => s);

        collector.Count.ShouldBe(1);
    }

    /// <summary>The filter matches the SDK record by its generated event name: an SDK upgrade that renames it reds here, not silently in production.</summary>
    [Fact]
    public void SdkStillLogsHandlerFailures_UnderTheMatchedEventName()
    {
        var method = typeof(McpServer).Assembly.GetType("ModelContextProtocol.McpSessionHandler")
            ?.GetMethod(CancelledRequestLogging.SdkEventName, BindingFlags.Instance | BindingFlags.NonPublic);

        method.ShouldNotBeNull();
        var attribute = method.GetCustomAttribute<LoggerMessageAttribute>();
        attribute.ShouldNotBeNull();
        attribute.EventName.ShouldBeNull();
        attribute.Message.ShouldContain("request handler failed");
    }

    private static (ILoggerFactory Factory, FakeLogCollector Collector) HostFactory()
    {
        var collector = new FakeLogCollector();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            HostLogging.Configure(builder, new InfrastructureOptions { DataRoot = Path.GetTempPath(), Scope = InstallScope.User });
            builder.ClearProviders();
            builder.AddProvider(new FakeLoggerProvider(collector));
        });
        return (services.BuildServiceProvider().GetRequiredService<ILoggerFactory>(), collector);
    }
}
