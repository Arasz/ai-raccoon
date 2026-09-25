using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     Auto-start (ADR-0020) only works when this process is the packaged apphost: the dotnet host
///     (`dotnet run`, `dotnet exec`, or a dotnet-tool shim published without an apphost) does not
///     understand ai-raccoon's own CLI shape, so spawning "&lt;host&gt; serve" as a child is doomed
///     before it starts.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackendLaunchArgumentsTests
{
    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData("DOTNET")]
    public void AnUnpackagedInvocation_IsDetected(string processPath) =>
        BackendLaunchArguments.IsUnpackagedInvocation(processPath).ShouldBeTrue();

    [Theory]
    [InlineData("/usr/local/bin/ai-raccoon")]
    [InlineData("AiRaccoon.exe")]
    [InlineData(null)]
    public void APackagedApphostOrUnknownPath_IsNotFlaggedAsUnpackaged(string? processPath) =>
        BackendLaunchArguments.IsUnpackagedInvocation(processPath).ShouldBeFalse();

    [Fact]
    public void Executable_ForAnUnpackagedProcessPath_ReturnsNull() =>
        BackendLaunchArguments.Executable("/usr/local/share/dotnet/dotnet").ShouldBeNull();

    [Fact]
    public void Executable_ForAPackagedApphostPath_ReturnsThatPath() =>
        BackendLaunchArguments.Executable("/usr/local/bin/ai-raccoon").ShouldBe("/usr/local/bin/ai-raccoon");

    [Fact]
    public void Executable_ForAnUnknownProcessPath_ReturnsNull() =>
        BackendLaunchArguments.Executable(null).ShouldBeNull();

    /// <summary>Pins the real seam: the no-arg overload reads live Environment.ProcessPath.</summary>
    [Fact]
    public void Executable_WithNoArguments_DelegatesToTheLiveProcessPath() =>
        BackendLaunchArguments.Executable().ShouldBe(BackendLaunchArguments.Executable(Environment.ProcessPath));

    /// <summary>
    ///     F70/K1: the private path pins --port 0 so the OS picks the ephemeral port the launcher
    ///     reads back from the child's stdout, while the attach path keeps the configured port.
    ///     Both carry the launch identity, --quiet included.
    /// </summary>
    [Fact]
    public void ServeArguments_PinTheConfiguredPort_AndPrivateArgumentsPinEphemeral()
    {
        var config = new ServerConfig(58432, McpTransport.Http,
            new InfrastructureOptions { DataRoot = "/tmp/some-root", Scope = InstallScope.User, Quiet = true });

        BackendLaunchArguments.ServeArguments(config).ShouldBe(
            ["--data-root", "/tmp/some-root", "--install-scope", "user", "--quiet", "serve", "--port", "58432"]);
        BackendLaunchArguments.PrivateServeArguments(config).ShouldBe(
            ["--data-root", "/tmp/some-root", "--install-scope", "user", "--quiet", "serve", "--port", "0"]);
    }

    [Fact]
    public void UnavailableExecutableMessage_ForAnUnpackagedInvocation_NamesTheShapeAndTheManualServeCommand()
    {
        var config = Config(port: 58432, dataRoot: "/tmp/some-root");

        var message = BackendLaunchArguments.UnavailableExecutableMessage("/usr/local/share/dotnet/dotnet", config);

        message.ShouldContain("dotnet host");
        message.ShouldContain("serve");
        message.ShouldContain("58432");
        message.ShouldContain("/tmp/some-root");
    }

    [Fact]
    public void UnavailableExecutableMessage_ForAGenuinelyUnknownPath_StaysGeneric()
    {
        var config = Config(port: 1, dataRoot: "/tmp/some-root");

        var message = BackendLaunchArguments.UnavailableExecutableMessage(null, config);

        message.ShouldContain("unknown");
        message.ShouldNotContain("dotnet host");
    }

    private static ServerConfig Config(int port, string dataRoot) =>
        new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User });

    // ── Executable fallback: the own process path was deleted by `dotnet tool update` (ADR-0116) ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GlobalToolShimPath_ForAnUnknownUserProfile_ReturnsNull(string? userProfileDirectory) =>
        BackendLaunchArguments.GlobalToolShimPath(userProfileDirectory).ShouldBeNull();

    [Fact]
    public void GlobalToolShimPath_JoinsTheUserProfileDotnetToolsAndTheExecutableFileName() =>
        BackendLaunchArguments.GlobalToolShimPath("/Users/rafal").ShouldBe(
            Path.Combine("/Users/rafal", ".dotnet", "tools", BackendLaunchArguments.ExecutableFileName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PathExecutable_ForAnUnknownPathVariable_ReturnsNull(string? pathVariable) =>
        BackendLaunchArguments.PathExecutable(pathVariable, _ => true).ShouldBeNull();

    [Fact]
    public void PathExecutable_ReturnsTheFirstDirectoryThatHoldsIt()
    {
        var pathVariable = string.Join(Path.PathSeparator, "/usr/bin", "/usr/local/bin");
        var expected = Path.Combine("/usr/local/bin", BackendLaunchArguments.ExecutableFileName);

        BackendLaunchArguments.PathExecutable(pathVariable, path => path == expected).ShouldBe(expected);
    }

    [Fact]
    public void PathExecutable_WhenNoDirectoryHoldsIt_ReturnsNull()
    {
        var pathVariable = string.Join(Path.PathSeparator, "/usr/bin", "/usr/local/bin");

        BackendLaunchArguments.PathExecutable(pathVariable, _ => false).ShouldBeNull();
    }

    [Fact]
    public void ResolveExecutable_WhenTheOwnPathExists_ReturnsItUnchanged_WithoutLogging()
    {
        var logger = new FakeLogger();

        var resolved = BackendLaunchArguments.ResolveExecutable("/opt/ai-raccoon/ai-raccoon", logger, _ => true, "/home/rafal", "/usr/bin");

        resolved.ShouldBe("/opt/ai-raccoon/ai-raccoon");
        logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public void ResolveExecutable_WhenTheOwnPathIsMissingButTheGlobalToolShimExists_FallsBackToTheShim_AndLogsOnce()
    {
        const string own = "/opt/ai-raccoon/.store/ai-raccoon/1.52.0/ai-raccoon/ai-raccoon";
        var shim = BackendLaunchArguments.GlobalToolShimPath("/home/rafal")!;
        var logger = new FakeLogger();

        var resolved = BackendLaunchArguments.ResolveExecutable(own, logger, path => path == shim, "/home/rafal", "/usr/bin");

        resolved.ShouldBe(shim);
        var record = logger.Collector.GetSnapshot().Single(r => r.Id == 693);
        record.Message.ShouldContain(own);
        record.Message.ShouldContain(shim);
    }

    [Fact]
    public void ResolveExecutable_WhenTheOwnPathIsMissingAndNoShimButFoundOnPath_FallsBackToThePathHit_AndLogsOnce()
    {
        const string own = "/opt/ai-raccoon/.store/ai-raccoon/1.52.0/ai-raccoon/ai-raccoon";
        var onPath = Path.Combine("/usr/local/bin", BackendLaunchArguments.ExecutableFileName);
        var logger = new FakeLogger();

        var resolved = BackendLaunchArguments.ResolveExecutable(own, logger, path => path == onPath, "/home/rafal", "/usr/local/bin");

        resolved.ShouldBe(onPath);
        logger.Collector.GetSnapshot().Single(r => r.Id == 693).Message.ShouldContain(own);
    }

    [Fact]
    public void ResolveExecutable_WhenNothingIsFound_ReturnsTheOwnPathUnchanged_WithoutLogging()
    {
        const string own = "/opt/ai-raccoon/.store/ai-raccoon/1.52.0/ai-raccoon/ai-raccoon";
        var logger = new FakeLogger();

        var resolved = BackendLaunchArguments.ResolveExecutable(own, logger, _ => false, "/home/rafal", "/usr/bin");

        resolved.ShouldBe(own);
        logger.Collector.GetSnapshot().ShouldBeEmpty();
    }
}
