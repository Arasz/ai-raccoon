using FluentValidation;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     One provider of search-parameter values. Null = no opinion; the next source decides.
///     Per-call values come from <see cref="SearchQuery" />; bank defaults come from the
///     store's settings snapshot (docs/work/2026-08-20-search-parameters-plan.md).
/// </summary>
public interface ISearchParametersSource
{
    int? RrfK { get; }

    int? FtsWeight { get; }

    int? VectorWeight { get; }

    double? SourceLambda { get; }

    double? ConsolidationThreshold { get; }

    DocScoreFormula? DocScoreFormula { get; }

    CandidateWindowMode? CandidateWindow { get; }

    double? StructureAlpha { get; }

    bool? FusionNoRegressionEnabled { get; }
}

/// <summary>
///     A search's resolved parameters: first non-null wins across the sources, then the
///     canonical constants, then validation. Every search runs on one of these.
/// </summary>
public sealed record SearchParameters(
    int RrfK,
    int FtsWeight,
    int VectorWeight,
    double SourceLambda,
    double ConsolidationThreshold,
    DocScoreFormula DocScoreFormula,
    CandidateWindowMode CandidateWindow,
    double StructureAlpha,
    bool FusionNoRegressionEnabled)
{
    /// <summary>
    ///     Resolves the options left to right: the first source with a value for an option
    ///     decides it; options no source provides fall back to the canonical constants.
    ///     Throws when no source is given or a resolved value fails validation.
    /// </summary>
    public static SearchParameters FromSources(params ISearchParametersSource[] sources)
    {
        if (sources.Length == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        var resolved = new SearchParameters(
            First(sources, source => source.RrfK) ?? SearchParameterSettingsKeys.DefaultRrfK,
            First(sources, source => source.FtsWeight) ?? SearchParameterSettingsKeys.DefaultFtsWeight,
            First(sources, source => source.VectorWeight) ?? SearchParameterSettingsKeys.DefaultVectorWeight,
            First(sources, source => source.SourceLambda) ?? SearchParameterSettingsKeys.DefaultSourceLambda,
            First(sources, source => source.ConsolidationThreshold)
                ?? SearchParameterSettingsKeys.DefaultConsolidationThreshold,
            First(sources, source => source.DocScoreFormula) ?? SearchParameterSettingsKeys.DefaultDocScoreFormula,
            First(sources, source => source.CandidateWindow) ?? SearchParameterSettingsKeys.DefaultCandidateWindow,
            First(sources, source => source.StructureAlpha) ?? SearchParameterSettingsKeys.DefaultStructureAlpha,
            First(sources, source => source.FusionNoRegressionEnabled)
                ?? SearchParameterSettingsKeys.DefaultFusionNoRegressionEnabled);

        Validator.Instance.ValidateAndThrow(resolved);
        return resolved;
    }

    private static T? First<T>(IReadOnlyList<ISearchParametersSource> sources, Func<ISearchParametersSource, T?> select)
        where T : struct
    {
        foreach (var source in sources)
        {
            var value = select(source);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    public sealed class Validator : AbstractValidator<SearchParameters>
    {
        public static readonly Validator Instance = new();

        public Validator()
        {
            RuleFor(x => x.RrfK).GreaterThan(0);
            RuleFor(x => x.FtsWeight).GreaterThanOrEqualTo(0);
            RuleFor(x => x.VectorWeight).GreaterThanOrEqualTo(0);
            RuleFor(x => x.SourceLambda).InclusiveBetween(0.0, 1.0);
            RuleFor(x => x.ConsolidationThreshold).GreaterThanOrEqualTo(0.0);
            RuleFor(x => x.StructureAlpha).InclusiveBetween(0.0, 1.0);
        }
    }
}
