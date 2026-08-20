using System.Globalization;
using AiRaccoon.Core.Memory.Fusion;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     Settings keys, parse helpers and fallback defaults for every search parameter
///     (docs/work/2026-08-20-search-parameters-plan.md). Mirrors the FusionConfigKeys
///     pattern: an absent or malformed setting reads as null so the caller falls back
///     — a bad setting can never crash a search.
/// </summary>
public static class SearchParameterSettingsKeys
{
    public const string RrfK = "retrieval.rrfK";
    public const string FtsWeight = "retrieval.ftsWeight";
    public const string VectorWeight = "retrieval.vectorWeight";
    public const string SourceLambda = "retrieval.sourceLambda";
    public const string ConsolidationThreshold = "retrieval.consolidationThreshold";
    public const string DocScoreFormula = "retrieval.docScoreFormula";
    public const string CandidateWindow = "retrieval.candidateWindow";
    public const string StructureAlpha = "retrieval.structureAlpha";

    // Kept at its original key for back-compat; the namespace inconsistency is deliberate.
    public const string FusionNoRegressionEnabled = FusionConfigKeys.NoRegressionEnabledGlobal;

    // Chosen values, docs/adr/0006 (pinned by SearchParameterSettingsKeysTests).
    // DefaultRrfK is aliased to SearchQuery's constant so the value lives in one place.
    public const int DefaultRrfK = SearchQuery.DefaultRrfK;
    public const int DefaultFtsWeight = 1;
    public const int DefaultVectorWeight = 1;
    public const double DefaultSourceLambda = 0.1;
    public const double DefaultConsolidationThreshold = 0.1;
    public const DocScoreFormula DefaultDocScoreFormula = global::AiRaccoon.Core.Memory.DocScoreFormula.Max;
    public const CandidateWindowMode DefaultCandidateWindow = CandidateWindowMode.Max3X100;
    public const double DefaultStructureAlpha = 0.5;
    public const bool DefaultFusionNoRegressionEnabled = FusionConfigKeys.DefaultNoRegressionEnabled;

    /// <summary>Parses a setting value, or null when absent, malformed or below *minInclusive*.</summary>
    public static int? ParseNullableInt(string? value, int minInclusive)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minInclusive)
        {
            return null;
        }

        return parsed;
    }

    /// <summary>Parses a setting value, or null when absent, malformed or outside [min, max].</summary>
    public static double? ParseNullableDouble(string? value, double min, double max)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min || parsed > max)
        {
            return null;
        }

        return parsed;
    }

    /// <summary>Parses the wire names "max"/"sum" (case-insensitive), or null.</summary>
    public static DocScoreFormula? ParseDocScoreFormula(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "max" => global::AiRaccoon.Core.Memory.DocScoreFormula.Max,
            "sum" => global::AiRaccoon.Core.Memory.DocScoreFormula.Sum,
            _ => null
        };

    /// <summary>Parses the wire names "max3x100"/"max5x50" (case-insensitive), or null.</summary>
    public static CandidateWindowMode? ParseCandidateWindow(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "max3x100" => CandidateWindowMode.Max3X100,
            "max5x50" => CandidateWindowMode.Max5X50,
            _ => null
        };

    /// <summary>Parses "true"/"false" (case-insensitive), or null.</summary>
    public static bool? ParseNullableBool(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };
}
