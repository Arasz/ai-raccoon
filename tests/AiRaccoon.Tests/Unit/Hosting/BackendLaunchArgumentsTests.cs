using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
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
}
