using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

public sealed partial class SqliteMemoryStore
{
    /// <summary>
    ///     The store's search-parameter defaults: one batched settings read on the search's
    ///     own connection (docs/work/2026-08-20-search-parameters-plan.md §3.2). A setting
    ///     that is absent or malformed stays null so <see cref="SearchParameters.FromSources" />
    ///     falls back to the canonical constants — a bad setting can never crash a search.
    /// </summary>
    internal async Task<ISearchParametersSource> GetSearchParameterDefaultsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<SettingRow>(
                    Def(MemorySql.SelectSettingsByPrefix, new { prefix = "retrieval." }, cancellationToken))
                .ConfigureAwait(false))
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var fusionFlag = await connection.QuerySingleOrDefaultAsync<string?>(
                Def(MemorySql.SelectSetting, new { key = FusionConfigKeys.NoRegressionEnabledGlobal }, cancellationToken))
            .ConfigureAwait(false);
        return new SettingsBackedSearchParameters(rows, fusionFlag);
    }

    /// <summary>A sparse settings snapshot: null per option when the key is absent or malformed.</summary>
    private sealed class SettingsBackedSearchParameters : ISearchParametersSource
    {
        private readonly IReadOnlyDictionary<string, string> _retrieval;
        private readonly string? _fusionFlag;

        public SettingsBackedSearchParameters(IReadOnlyDictionary<string, string> retrieval, string? fusionFlag)
        {
            _retrieval = retrieval;
            _fusionFlag = fusionFlag;
        }

        public int? RrfK => SearchParameterSettingsKeys.ParseNullableInt(Get(SearchParameterSettingsKeys.RrfK), 1);
        public int? FtsWeight => SearchParameterSettingsKeys.ParseNullableInt(Get(SearchParameterSettingsKeys.FtsWeight), 0);
        public int? VectorWeight => SearchParameterSettingsKeys.ParseNullableInt(Get(SearchParameterSettingsKeys.VectorWeight), 0);
        public double? SourceLambda =>
            SearchParameterSettingsKeys.ParseNullableDouble(Get(SearchParameterSettingsKeys.SourceLambda), 0.0, 1.0);
        public double? ConsolidationThreshold => SearchParameterSettingsKeys.ParseNullableDouble(
            Get(SearchParameterSettingsKeys.ConsolidationThreshold), 0.0, double.MaxValue);
        public DocScoreFormula? DocScoreFormula => SearchParameterSettingsKeys.ParseDocScoreFormula(
            Get(SearchParameterSettingsKeys.DocScoreFormula));
        public CandidateWindowMode? CandidateWindow => SearchParameterSettingsKeys.ParseCandidateWindow(
            Get(SearchParameterSettingsKeys.CandidateWindow));
        public double? StructureAlpha =>
            SearchParameterSettingsKeys.ParseNullableDouble(Get(SearchParameterSettingsKeys.StructureAlpha), 0.0, 1.0);
        public bool? FusionNoRegressionEnabled => SearchParameterSettingsKeys.ParseNullableBool(_fusionFlag);

        private string? Get(string key) => _retrieval.GetValueOrDefault(key);
    }

    private sealed record SettingRow(string Key, string Value);
}
