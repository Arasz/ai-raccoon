using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     The reserved shared target's scope contract: the one id it owns, the context its rows live
///     in, and the operations it refuses. Every component that routes a write or a read depends
///     on this contract, so a drift here moves a row's scope.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SharedProjectScopeTests
{
    private readonly ISharedProjectScope _scope = new SharedProjectScope();

    [Theory]
    [InlineData("shared", true)]
    [InlineData("Shared", false)]
    [InlineData("SHARED", false)]
    [InlineData(" shared", false)]
    [InlineData("shared-documents", false)]
    [InlineData("acme", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReserved_MatchesTheExactReservedIdOnly(string? projectId, bool expected) =>
        _scope.IsReserved(projectId).ShouldBe(expected);

    [Fact]
    public void ContextFor_RoutesTheReservedIdToTheSharedContext_AndEveryOtherIdToItsOwn()
    {
        _scope.ContextFor("shared").ShouldBe(ContextNaming.SharedContext);
        _scope.ContextFor("acme").ShouldBe(ContextNaming.ProjectContext("acme"));
    }

    [Fact]
    public void RequireSearch_AllowsTheReservedId_AllAndSharedScopes()
    {
        Should.NotThrow(() => _scope.RequireSearch(new SearchQuery("shared", "example", Scope: SearchScope.All)));
        Should.NotThrow(() => _scope.RequireSearch(new SearchQuery("shared", "example", Scope: SearchScope.Shared)));
    }

    [Fact]
    public void RequireSearch_RefusesTheReservedIdWithAProjectScope() =>
        Should.Throw<ArgumentException>(() =>
            _scope.RequireSearch(new SearchQuery("shared", "example", Scope: SearchScope.Project)));

    [Fact]
    public void RequireSearch_RefusesTheReservedIdWithAWorkspaceOrALabel()
    {
        Should.Throw<ArgumentException>(() =>
            _scope.RequireSearch(new SearchQuery("shared", "example", WorkspaceId: "scratch")));
        Should.Throw<ArgumentException>(() =>
            _scope.RequireSearch(new SearchQuery("shared", "example", ContextLabel: "docs")));
    }

    [Fact]
    public void RequireSearch_LeavesOrdinaryProjectsAlone()
    {
        Should.NotThrow(() => _scope.RequireSearch(new SearchQuery("acme", "example", Scope: SearchScope.Project)));
        Should.NotThrow(() =>
            _scope.RequireSearch(new SearchQuery("acme", "example", WorkspaceId: "scratch", ContextLabel: "docs")));
    }

    [Fact]
    public void RequireOrdinary_RefusesTheReservedId_AndNamesTheOperation()
    {
        var error = Record.Exception(() => _scope.RequireOrdinary("shared", "note writes"));
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("shared-project-unsupported");
        error.Message.ShouldContain("note writes");
    }

    [Fact]
    public void RequireOrdinary_LeavesOrdinaryProjectsAlone() =>
        Should.NotThrow(() => _scope.RequireOrdinary("acme", "note writes"));

    [Fact]
    public void TheCompositionRoot_ResolvesTheScopeContract()
    {
        var tempRoot = TestData.CreateTempRoot();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(tempRoot));

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ISharedProjectScope>().ShouldBeOfType<SharedProjectScope>();
        }
        finally
        {
            TestData.DeleteTempRoot(tempRoot);
        }
    }
}
