namespace AiRaccoon.Core.Memory;

/// <summary>
///     The bank's search-parameter settings, one nullable read per option: null means the
///     key is absent or malformed, and the caller falls back (SearchParameterSettingsKeys).
///     Implemented over the settings table by the infrastructure layer; the per-search path
///     uses the store's batched connection-scoped snapshot instead of this interface
///     (docs/work/2026-08-20-search-parameters-plan.md §3.2).
/// </summary>
public interface ISearchParametersSettings
{
    Task<int?> GetRrfKAsync(CancellationToken cancellationToken = default);

    Task<int?> GetFtsWeightAsync(CancellationToken cancellationToken = default);

    Task<int?> GetVectorWeightAsync(CancellationToken cancellationToken = default);

    Task<double?> GetSourceLambdaAsync(CancellationToken cancellationToken = default);

    Task<double?> GetConsolidationThresholdAsync(CancellationToken cancellationToken = default);

    Task<DocScoreFormula?> GetDocScoreFormulaAsync(CancellationToken cancellationToken = default);

    Task<CandidateWindowMode?> GetCandidateWindowAsync(CancellationToken cancellationToken = default);

    Task<double?> GetStructureAlphaAsync(CancellationToken cancellationToken = default);

    Task<bool?> GetFusionNoRegressionEnabledAsync(CancellationToken cancellationToken = default);
}
