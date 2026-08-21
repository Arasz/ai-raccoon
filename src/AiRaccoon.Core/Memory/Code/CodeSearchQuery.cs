namespace AiRaccoon.Core.Memory.Code;

/// <summary>
///     Code corpus search is always project-scoped (§3.1) — no scope/workspace parameter. Per-call
///     tuning knobs are limit/minRelativeScore only (§3.6): every other search parameter
///     (rrfK/ftsWeight/vectorWeight/etc.) has "no opinion" here, so <see cref="SearchParameters.FromSources" />
///     falls straight through to the bank's retrieval.* settings — the same ones memory search
///     tunes (§12.2 H7). <see cref="ISearchParametersSource.StructureAlpha" /> resolves but is
///     never applied anywhere in the code vector query — code has no structure modality (§3.1).
/// </summary>
public sealed record CodeSearchQuery(string ProjectId, string Query, int Limit, double MinRelativeScore)
    : ISearchParametersSource
{
    int? ISearchParametersSource.RrfK => null;
    int? ISearchParametersSource.FtsWeight => null;
    int? ISearchParametersSource.VectorWeight => null;
    double? ISearchParametersSource.SourceLambda => null;
    double? ISearchParametersSource.ConsolidationThreshold => null;
    DocScoreFormula? ISearchParametersSource.DocScoreFormula => null;
    CandidateWindowMode? ISearchParametersSource.CandidateWindow => null;
    double? ISearchParametersSource.StructureAlpha => null;
    bool? ISearchParametersSource.FusionNoRegressionEnabled => null;
}
