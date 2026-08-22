using AiRaccoon.Hosting.Common;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     Auto-start (ADR-0020) only works when this process is the packaged apphost: the dotnet muxer
///     (`dotnet run`, `dotnet exec`) does not understand ai-raccoon's own CLI shape, so spawning
///     "&lt;muxer&gt; serve" as a child is doomed before it starts.
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
}
