using FluentValidation;

namespace AiRaccoon.Core.Memory;

public sealed record SearchQuery(
    string ProjectId,
    string Query,
    SearchScope Scope = SearchScope.All,
    string? WorkspaceId = null,
    int Limit = 20,
    double MinScore = 0.7)
{
    public const int DefaultRrfK = 60;

    public SearchQuery(
        string projectId,
        string query,
        SearchScope scope = SearchScope.All,
        string? workspaceId = null,
        int limit = 20,
        double minScore = 0.7,
        int rrfK = DefaultRrfK,
        int ftsWeight = 1,
        int vectorWeight = 1)
    {
        ProjectId = projectId;
        Query = query;
        Scope = scope;
        WorkspaceId = workspaceId;
        Limit = limit;
        MinScore = minScore;
        RrfK = rrfK;
        FtsWeight = ftsWeight;
        VectorWeight = vectorWeight;
    }

    public string ProjectId { get; }

    public string Query { get; }

    public SearchScope Scope { get; }

    public string? WorkspaceId { get; }

    public int Limit { get; }

    public double MinScore { get; }

    /// <summary>RRF cutoff; a result's score contribution from a ranked list is weight / (k + rank).</summary>
    public int RrfK { get; }

    /// <summary>Weight of the FTS5 (keyword) ranked list in the RRF fusion.</summary>
    public int FtsWeight { get; }

    /// <summary>Weight of the vec0 (semantic) ranked list in the RRF fusion.</summary>
    public int VectorWeight { get; }

    public sealed class Validator : AbstractValidator<SearchQuery>
    {
        public Validator()
        {
            RuleFor(x => x.ProjectId).NotNull().NotEmpty();
            RuleFor(x => x.Query).NotNull().NotEmpty();
            RuleFor(x => x.Limit).GreaterThan(0);
            RuleFor(x => x.MinScore).InclusiveBetween(0.0, 1.0);
            RuleFor(x => x.RrfK).GreaterThan(0);
            RuleFor(x => x.FtsWeight).GreaterThanOrEqualTo(0);
            RuleFor(x => x.VectorWeight).GreaterThanOrEqualTo(0);
        }
    }
}