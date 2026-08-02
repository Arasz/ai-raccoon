using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Domain;

public class SearchQueryTests
{
    [Fact]
    public void Constructor_WithValidValues_KeepsThem()
    {
        var query = new SearchQuery("acme", "vector search", workspaceId: "ws-1", limit: 5, minScore: 0.9);

        query.ProjectId.ShouldBe("acme");
        query.Query.ShouldBe("vector search");
        query.WorkspaceId.ShouldBe("ws-1");
        query.Limit.ShouldBe(5);
        query.MinScore.ShouldBe(0.9);
    }

    [Fact]
    public void Constructor_WithOnlyRequiredFields_AppliesDefaults()
    {
        var query = new SearchQuery("acme", "search");

        query.WorkspaceId.ShouldBeNull();
        query.Limit.ShouldBe(20);
        query.MinScore.ShouldBe(0.7);
        query.Scope.ShouldBe(SearchScope.All);
    }

    [Fact]
    public void Constructor_WithScope_KeepsIt()
    {
        var query = new SearchQuery("acme", "search", scope: SearchScope.Shared);

        query.Scope.ShouldBe(SearchScope.Shared);
    }

    [Fact]
    public void Constructor_WithProjectScope_KeepsIt()
    {
        var query = new SearchQuery("acme", "search", scope: SearchScope.Project);

        query.Scope.ShouldBe(SearchScope.Project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithBlankProjectId_Throws(string? projectId)
    {
        Should.Throw<ArgumentException>(() => new SearchQuery(projectId!, "query"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithBlankQuery_Throws(string? query)
    {
        Should.Throw<ArgumentException>(() => new SearchQuery("acme", query!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveLimit_Throws(int limit)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SearchQuery("acme", "query", limit: limit));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Constructor_WithMinScoreOutsideUnitInterval_Throws(double minScore)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SearchQuery("acme", "query", minScore: minScore));
    }
}
