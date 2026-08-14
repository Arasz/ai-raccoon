using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SearchQueryTests
{
    [Fact]
    public void Constructor_WithValidValues_KeepsThem()
    {
        var query = new SearchQuery("acme", "vector search", WorkspaceId: "ws-1", Limit: 5, MinRelativeScore: 0.9);

        query.ProjectId.ShouldBe("acme");
        query.Query.ShouldBe("vector search");
        query.WorkspaceId.ShouldBe("ws-1");
        query.Limit.ShouldBe(5);
        query.MinRelativeScore.ShouldBe(0.9);
    }

    [Fact]
    public void Constructor_WithOnlyRequiredFields_AppliesDefaults()
    {
        var query = new SearchQuery("acme", "search");

        query.WorkspaceId.ShouldBeNull();
        query.Limit.ShouldBe(20);
        query.MinRelativeScore.ShouldBe(0.0);
        query.Scope.ShouldBe(SearchScope.All);
        query.RrfK.ShouldBe(60);
        query.FtsWeight.ShouldBe(1);
        query.VectorWeight.ShouldBe(1);
        query.CandidateWindow.ShouldBe(CandidateWindowMode.Max3X100);
    }

    [Fact]
    public void Constructor_WithCandidateWindow_KeepsIt()
    {
        var query = new SearchQuery("acme", "search", CandidateWindow: CandidateWindowMode.Max5X50);

        query.CandidateWindow.ShouldBe(CandidateWindowMode.Max5X50);
    }

    [Fact]
    public void Constructor_WithFusionParameters_KeepsThem()
    {
        var query = new SearchQuery("acme", "search", RrfK: 30, FtsWeight: 2, VectorWeight: 1);

        query.RrfK.ShouldBe(30);
        query.FtsWeight.ShouldBe(2);
        query.VectorWeight.ShouldBe(1);
    }

    [Fact]
    public void Constructor_WithScope_KeepsIt()
    {
        var query = new SearchQuery("acme", "search", SearchScope.Shared);

        query.Scope.ShouldBe(SearchScope.Shared);
    }

    [Fact]
    public void Constructor_WithProjectScope_KeepsIt()
    {
        var query = new SearchQuery("acme", "search", SearchScope.Project);

        query.Scope.ShouldBe(SearchScope.Project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validator_WithBlankProjectId_ReportsCamelCaseProperty(string? projectId)
    {
        var result = new SearchQuery.Validator().Validate(new SearchQuery(projectId!, "query"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "projectId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validator_WithBlankQuery_ReportsCamelCaseProperty(string? query)
    {
        var result = new SearchQuery.Validator().Validate(new SearchQuery("acme", query!));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "query");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validator_WithNonPositiveLimit_ReportsCamelCaseProperty(int limit)
    {
        var result = new SearchQuery.Validator().Validate(new SearchQuery("acme", "query", Limit: limit));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "limit");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validator_WithMinRelativeScoreOutsideUnitInterval_ReportsCamelCaseProperty(double minRelativeScore)
    {
        var result = new SearchQuery.Validator().Validate(new SearchQuery("acme", "query", MinRelativeScore: minRelativeScore));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "minRelativeScore");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validator_WithNonPositiveRrfK_ReportsCamelCaseProperty(int rrfK)
    {
        var result = new SearchQuery.Validator().Validate(new SearchQuery("acme", "query", RrfK: rrfK));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "rrfK");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Validator_WithNegativeFusionWeight_ReportsCamelCaseProperty(int weight)
    {
        var result = new SearchQuery.Validator()
            .Validate(new SearchQuery("acme", "query", FtsWeight: weight, VectorWeight: 1));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "ftsWeight");
    }

    [Fact]
    public void Validator_WithValidValues_Passes()
    {
        var result = new SearchQuery.Validator().Validate(new SearchQuery("acme", "query", Limit: 5, MinRelativeScore: 0.9));

        result.IsValid.ShouldBeTrue();
    }
}
