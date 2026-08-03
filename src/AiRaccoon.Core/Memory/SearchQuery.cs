using FluentValidation;

namespace AiRaccoon.Core.Memory;

public sealed record SearchQuery
{
    public SearchQuery(
        string projectId,
        string query,
        SearchScope scope = SearchScope.All,
        string? workspaceId = null,
        int limit = 20,
        double minScore = 0.7)
    {
        ProjectId = projectId;
        Query = query;
        Scope = scope;
        WorkspaceId = workspaceId;
        Limit = limit;
        MinScore = minScore;
    }

    public string ProjectId { get; }

    public string Query { get; }

    public SearchScope Scope { get; }

    public string? WorkspaceId { get; }

    public int Limit { get; }

    public double MinScore { get; }

    public sealed class Validator : AbstractValidator<SearchQuery>
    {
        public Validator()
        {
            RuleFor(x => x.ProjectId).NotNull().NotEmpty();
            RuleFor(x => x.Query).NotNull().NotEmpty();
            RuleFor(x => x.Limit).GreaterThan(0);
            RuleFor(x => x.MinScore).InclusiveBetween(0.0, 1.0);
        }
    }
}
