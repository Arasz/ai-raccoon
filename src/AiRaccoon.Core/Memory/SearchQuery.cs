using FluentValidation;

namespace AiRaccoon.Core.Memory;

public sealed record SearchQuery(
    string ProjectId,
    string Query,
    SearchScope Scope = SearchScope.All,
    string? WorkspaceId = null,
    int Limit = 20,
    double MinScore = 0.7,
    int RrfK = SearchQuery.DefaultRrfK,
    int FtsWeight = 1,
    int VectorWeight = 1)
{
    public const int DefaultRrfK = 60;

    /// <summary>RRF cutoff; a result's score contribution from a ranked list is weight / (k + rank).</summary>
    public int RrfK { get; } = RrfK;

    /// <summary>Weight of the FTS5 (keyword) ranked list in the RRF fusion.</summary>
    public int FtsWeight { get; } = FtsWeight;

    /// <summary>Weight of the vec0 (semantic) ranked list in the RRF fusion.</summary>
    public int VectorWeight { get; } = VectorWeight;

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
