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
    public void EnsureCanExecuteInSharedScope_AllowsTheReservedId_AllAndSharedScopes()
    {
        Should.NotThrow(() => _scope.EnsureCanExecuteInSharedScope(new SearchQuery("shared", "example", Scope: SearchScope.All)));
        Should.NotThrow(() => _scope.EnsureCanExecuteInSharedScope(new SearchQuery("shared", "example", Scope: SearchScope.Shared)));
    }

    [Fact]
    public void EnsureCanExecuteInSharedScope_RefusesTheReservedIdWithAProjectScope() =>
        Should.Throw<ArgumentException>(() =>
            _scope.EnsureCanExecuteInSharedScope(new SearchQuery("shared", "example", Scope: SearchScope.Project)));

    [Fact]
    public void EnsureCanExecuteInSharedScope_RefusesTheReservedIdWithAWorkspaceOrALabel()
    {
        Should.Throw<ArgumentException>(() =>
            _scope.EnsureCanExecuteInSharedScope(new SearchQuery("shared", "example", WorkspaceId: "scratch")));
        Should.Throw<ArgumentException>(() =>
            _scope.EnsureCanExecuteInSharedScope(new SearchQuery("shared", "example", ContextLabel: "docs")));
    }

    [Fact]
    public void EnsureCanExecuteInSharedScope_SearchLeavesOrdinaryProjectsAlone()
    {
        Should.NotThrow(() => _scope.EnsureCanExecuteInSharedScope(new SearchQuery("acme", "example", Scope: SearchScope.Project)));
        Should.NotThrow(() =>
            _scope.EnsureCanExecuteInSharedScope(new SearchQuery("acme", "example", WorkspaceId: "scratch", ContextLabel: "docs")));
    }

    [Fact]
    public void EnsureCanExecuteInSharedScope_RefusesTheReservedId_AndNamesTheOperation()
    {
        var error = Record.Exception(() => _scope.EnsureCanExecuteInSharedScope("shared", "note writes"));
        error.ShouldNotBeNull();
        error!.Message.ShouldContain("shared-project-unsupported");
        error.Message.ShouldContain("note writes");
    }

    [Theory]
    [InlineData("shared", false)]
    [InlineData("Shared", true)]
    [InlineData("acme", true)]
    public void CanExecuteInSharedScope_OrdinaryOnlyOperation_ReturnsDecision(string projectId, bool allowed) =>
        _scope.CanExecuteInSharedScope(new MemoryWriteRequest(projectId, "note")).ShouldBe(allowed);

    [Theory]
    [InlineData("shared", SearchScope.All, null, null, true)]
    [InlineData("shared", SearchScope.Shared, null, null, true)]
    [InlineData("shared", SearchScope.Project, null, null, false)]
    [InlineData("shared", SearchScope.All, "scratch", null, false)]
    [InlineData("shared", SearchScope.All, null, "docs", false)]
    [InlineData("shared", SearchScope.Shared, " ", " ", true)]
    [InlineData("acme", SearchScope.All, null, null, true)]
    [InlineData("acme", SearchScope.Shared, null, null, true)]
    [InlineData("acme", SearchScope.Project, "scratch", "docs", true)]
    public void CanExecuteInSharedScope_Search_ReturnsDecision(string projectId, SearchScope searchScope,
        string? workspaceId, string? contextLabel, bool allowed) =>
        _scope.CanExecuteInSharedScope(new SearchQuery(projectId, "example", Scope: searchScope,
            WorkspaceId: workspaceId, ContextLabel: contextLabel)).ShouldBe(allowed);

    [Fact]
    public void EnsureCanExecuteInSharedScope_PreservesOperationAndSearchMessages()
    {
        Should.Throw<ArgumentException>(() => _scope.EnsureCanExecuteInSharedScope(
            new MemoryWriteRequest("shared", "note")))
            .Message.ShouldBe("shared-project-unsupported: note writes requires an ordinary project; shared supports document ingestion and watches.");
        Should.NotThrow(() => _scope.EnsureCanExecuteInSharedScope(new MemoryWriteRequest("acme", "note")));
        Should.Throw<ArgumentException>(() => _scope.EnsureCanExecuteInSharedScope("shared", "add_memory"))
            .Message.ShouldBe("shared-project-unsupported: add_memory requires an ordinary project; shared supports document ingestion and watches.");
        Should.Throw<ArgumentException>(() => _scope.EnsureCanExecuteInSharedScope(
            new SearchQuery("shared", "example", Scope: SearchScope.Project)))
            .Message.ShouldBe("shared-project-unsupported: search shared with scope all or shared, without a workspace or label.");
    }

    [Fact]
    public void EnsureCanExecuteInSharedScope_LeavesOrdinaryProjectsAlone() =>
        Should.NotThrow(() => _scope.EnsureCanExecuteInSharedScope("acme", "note writes"));

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
