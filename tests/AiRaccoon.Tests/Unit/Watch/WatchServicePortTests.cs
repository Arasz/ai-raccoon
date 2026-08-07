using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Pins the watch error types the port raises (S2 surface).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchServicePortTests
{
    [Fact]
    public void WatchDisabledException_CarriesProjectIdInMessage() =>
        new WatchDisabledException("acme").Message.ShouldContain("acme");

    [Fact]
    public void PathOutsideScopeException_CarriesPathInMessage() =>
        new PathOutsideScopeException("/repo").Message.ShouldContain("/repo");

    [Fact]
    public void PathNotFound_CarriesPathInMessage() =>
        new PathNotFound("/repo").Message.ShouldContain("/repo");

    [Fact]
    public void ErrorTypes_AreInvalidOperationExceptions()
    {
        new WatchDisabledException("acme").ShouldBeAssignableTo<InvalidOperationException>();
        new PathOutsideScopeException("/repo").ShouldBeAssignableTo<InvalidOperationException>();
        new PathNotFound("/repo").ShouldBeAssignableTo<InvalidOperationException>();
    }
}
