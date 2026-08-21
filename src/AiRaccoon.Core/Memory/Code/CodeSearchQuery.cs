namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     Code corpus search is always project-scoped (§3.1) — no scope/workspace parameter.
///     limit/minRelativeScore get a genuinely separate per-section override (§3.6); every other
///     per-call tuning knob (rrfK/ftsWeight/vectorWeight/candidateWindow) applies to the code
///     section too, at the SAME value the caller gave memory's — no separate codeRrfK/etc.
///     namespace in v1 (ADR-0088 decision 5). Values left unset (null) fall through
///     <see cref="SearchParameters.FromSources" /> to the bank's retrieval.* settings, the same
///     ones memory search tunes (§12.2 H7). <see cref="ISearchParametersSource.StructureAlpha" />
///     resolves but is never applied anywhere in the code vector query — code has no structure
///     modality (§3.1).
/// </summary>
public sealed record CodeSearchQuery(
    string ProjectId,
    string Query,
    int Limit,
    double MinRelativeScore,
    int? RrfK = null,
    int? FtsWeight = null,
    int? VectorWeight = null,
    CandidateWindowMode? CandidateWindow = null) : ISearchParametersSource
{
    int? ISearchParametersSource.RrfK => RrfK;
    int? ISearchParametersSource.FtsWeight => FtsWeight;
    int? ISearchParametersSource.VectorWeight => VectorWeight;
    double? ISearchParametersSource.SourceLambda => null;
    double? ISearchParametersSource.ConsolidationThreshold => null;
    DocScoreFormula? ISearchParametersSource.DocScoreFormula => null;
    CandidateWindowMode? ISearchParametersSource.CandidateWindow => CandidateWindow;
    double? ISearchParametersSource.StructureAlpha => null;
    bool? ISearchParametersSource.FusionNoRegressionEnabled => null;
}
