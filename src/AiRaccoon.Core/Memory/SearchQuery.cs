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
    int VectorWeight = 1,
    string? ContextLabel = null,
    double SourceLambda = 0.1,
    double ConsolidationThreshold = 0.1,
    DocScoreFormula DocScoreFormula = DocScoreFormula.Max,
    CandidateWindowMode CandidateWindow = CandidateWindowMode.Max3x100)
{
    public const int DefaultRrfK = 60;

    /// <summary>RRF cutoff; a result's score contribution from a ranked list is weight / (k + rank).</summary>
    public int RrfK { get; } = RrfK;

    /// <summary>Weight of the FTS5 (keyword) ranked list in the RRF fusion.</summary>
    public int FtsWeight { get; } = FtsWeight;

    /// <summary>Weight of the vec0 (semantic) ranked list in the RRF fusion.</summary>
    public int VectorWeight { get; } = VectorWeight;

    /// <summary>When set, the project scope also searches this project's custom-scoped rows under the label.</summary>
    public string? ContextLabel { get; } = ContextLabel;

    /// <summary>Adjacent-chunk boost: a same-source sibling at chunk index N±1 adds λ to the chunk's score.</summary>
    public double SourceLambda { get; } = SourceLambda;

    /// <summary>Consolidation threshold: sibling visibility floor and the merge gap for weak adjacent siblings.</summary>
    public double ConsolidationThreshold { get; } = ConsolidationThreshold;

    /// <summary>Document-score formula used as the secondary sort key.</summary>
    public DocScoreFormula DocScoreFormula { get; } = DocScoreFormula;

    /// <summary>Per-modality candidate depth policy before RRF fusion (see docs/adr/0006-rrf-parameter-optimization.md).</summary>
    public CandidateWindowMode CandidateWindow { get; } = CandidateWindow;

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
            RuleFor(x => x.ContextLabel).MaximumLength(256);
            RuleFor(x => x.SourceLambda).InclusiveBetween(0.0, 1.0);
            RuleFor(x => x.ConsolidationThreshold).GreaterThanOrEqualTo(0.0);
        }
    }
}
