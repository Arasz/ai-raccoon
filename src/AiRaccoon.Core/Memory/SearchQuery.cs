using FluentValidation;

namespace AiRaccoon.Core.Memory;

public sealed record SearchQuery(
    string ProjectId,
    string Query,
    SearchScope Scope = SearchScope.All,
    string? WorkspaceId = null,
    int Limit = SearchDefaults.Limit,
    double MinRelativeScore = SearchDefaults.MinRelativeScore,
    int? RrfK = null,
    int? FtsWeight = null,
    int? VectorWeight = null,
    string? ContextLabel = null,
    double? SourceLambda = null,
    double? ConsolidationThreshold = null,
    DocScoreFormula? DocScoreFormula = null,
    CandidateWindowMode? CandidateWindow = null) : ISearchParametersSource
{
    public const int DefaultRrfK = 60;

    /// <summary>
    ///     Floor on the max-normalized ranking, i.e. a fraction of this response's top hit — relative,
    ///     never an absolute quality bar. 0 disables it (see docs/adr/0047-relative-score-floor.md).
    /// </summary>
    public double MinRelativeScore { get; } = MinRelativeScore;

    /// <summary>When set, the project scope also searches this project's custom-scoped rows under the label.</summary>
    public string? ContextLabel { get; } = ContextLabel;

    // Per-call tuning values; null means "no opinion" and the store's defaults decide
    // (SearchParameters.FromSources). Bank policy options (structure alpha, fusion flag)
    // are never per-call and report no opinion.

    int? ISearchParametersSource.RrfK => RrfK;
    int? ISearchParametersSource.FtsWeight => FtsWeight;
    int? ISearchParametersSource.VectorWeight => VectorWeight;
    double? ISearchParametersSource.SourceLambda => SourceLambda;
    double? ISearchParametersSource.ConsolidationThreshold => ConsolidationThreshold;
    DocScoreFormula? ISearchParametersSource.DocScoreFormula => DocScoreFormula;
    CandidateWindowMode? ISearchParametersSource.CandidateWindow => CandidateWindow;
    double? ISearchParametersSource.StructureAlpha => null;
    bool? ISearchParametersSource.FusionNoRegressionEnabled => null;

    public sealed class Validator : AbstractValidator<SearchQuery>
    {
        public Validator()
        {
            RuleFor(x => x.ProjectId).NotNull().NotEmpty();
            RuleFor(x => x.Query).NotNull().NotEmpty();
            RuleFor(x => x.Limit).GreaterThan(0);
            RuleFor(x => x.MinRelativeScore).InclusiveBetween(0.0, 1.0);
            RuleFor(x => x.ContextLabel).MaximumLength(256);
        }
    }
}
