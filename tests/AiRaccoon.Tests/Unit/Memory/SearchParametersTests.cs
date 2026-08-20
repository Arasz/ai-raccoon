using AiRaccoon.Core.Memory;
using FluentValidation;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SearchParametersTests
{
    [Fact]
    public void FromSources_WithQueryAndDefaults_QueryValuesWin()
    {
        var query = new StubSource(rrfK: 10, ftsWeight: 3);
        var defaults = new StubSource(rrfK: 60, ftsWeight: 1, vectorWeight: 1, sourceLambda: 0.1,
            consolidationThreshold: 0.1, docScoreFormula: DocScoreFormula.Max,
            candidateWindow: CandidateWindowMode.Max3X100, structureAlpha: 0.5,
            fusionNoRegressionEnabled: false);

        var resolved = SearchParameters.FromSources(query, defaults);

        resolved.RrfK.ShouldBe(10);
        resolved.FtsWeight.ShouldBe(3);
        resolved.VectorWeight.ShouldBe(1);
        resolved.SourceLambda.ShouldBe(0.1);
        resolved.ConsolidationThreshold.ShouldBe(0.1);
        resolved.DocScoreFormula.ShouldBe(DocScoreFormula.Max);
        resolved.CandidateWindow.ShouldBe(CandidateWindowMode.Max3X100);
        resolved.StructureAlpha.ShouldBe(0.5);
        resolved.FusionNoRegressionEnabled.ShouldBeFalse();
    }

    [Fact]
    public void FromSources_WithOnlyQuery_UsesCanonicalConstantsForUnprovidedOptions()
    {
        var resolved = SearchParameters.FromSources(new StubSource(rrfK: 25));

        resolved.RrfK.ShouldBe(25);
        resolved.FtsWeight.ShouldBe(SearchParameterSettingsKeys.DefaultFtsWeight);
        resolved.VectorWeight.ShouldBe(SearchParameterSettingsKeys.DefaultVectorWeight);
        resolved.SourceLambda.ShouldBe(SearchParameterSettingsKeys.DefaultSourceLambda);
        resolved.ConsolidationThreshold.ShouldBe(SearchParameterSettingsKeys.DefaultConsolidationThreshold);
        resolved.DocScoreFormula.ShouldBe(SearchParameterSettingsKeys.DefaultDocScoreFormula);
        resolved.CandidateWindow.ShouldBe(SearchParameterSettingsKeys.DefaultCandidateWindow);
        resolved.StructureAlpha.ShouldBe(SearchParameterSettingsKeys.DefaultStructureAlpha);
        resolved.FusionNoRegressionEnabled.ShouldBe(SearchParameterSettingsKeys.DefaultFusionNoRegressionEnabled);
    }

    [Fact]
    public void FromSources_WithSettingsSource_SettingsOverrideConstants()
    {
        var defaults = new StubSource(sourceLambda: 0.3, consolidationThreshold: 0.05,
            docScoreFormula: DocScoreFormula.Sum, candidateWindow: CandidateWindowMode.Max5X50);

        var resolved = SearchParameters.FromSources(new StubSource(), defaults);

        resolved.SourceLambda.ShouldBe(0.3);
        resolved.ConsolidationThreshold.ShouldBe(0.05);
        resolved.DocScoreFormula.ShouldBe(DocScoreFormula.Sum);
        resolved.CandidateWindow.ShouldBe(CandidateWindowMode.Max5X50);
        resolved.RrfK.ShouldBe(SearchParameterSettingsKeys.DefaultRrfK);
        resolved.StructureAlpha.ShouldBe(SearchParameterSettingsKeys.DefaultStructureAlpha);
        resolved.FusionNoRegressionEnabled.ShouldBeFalse();
    }

    [Fact]
    public void FromSources_FirstNonNullWins_LeftToRight()
    {
        var first = new StubSource(ftsWeight: 5);
        var second = new StubSource(ftsWeight: 7);

        var resolved = SearchParameters.FromSources(first, second);

        resolved.FtsWeight.ShouldBe(5);
    }

    [Fact]
    public void FromSources_WithNoSources_Throws()
    {
        var exception = Should.Throw<ArgumentException>(() => SearchParameters.FromSources());

        exception.Message.ShouldContain("at least one");
    }

    [Fact]
    public void FromSources_WithInvalidRrfK_Throws()
    {
        Should.Throw<ValidationException>(() => SearchParameters.FromSources(new StubSource(rrfK: 0)));
    }

    [Fact]
    public void FromSources_WithNegativeWeight_Throws()
    {
        Should.Throw<ValidationException>(() => SearchParameters.FromSources(new StubSource(ftsWeight: -1)));
        Should.Throw<ValidationException>(() => SearchParameters.FromSources(new StubSource(vectorWeight: -1)));
    }

    [Fact]
    public void FromSources_WithOutOfRangeLambda_Throws()
    {
        Should.Throw<ValidationException>(() => SearchParameters.FromSources(new StubSource(sourceLambda: 2.0)));
        Should.Throw<ValidationException>(() => SearchParameters.FromSources(new StubSource(sourceLambda: -0.1)));
    }

    [Fact]
    public void FromSources_WithNegativeConsolidationThreshold_Throws()
    {
        Should.Throw<ValidationException>(() =>
            SearchParameters.FromSources(new StubSource(consolidationThreshold: -0.5)));
    }

    [Fact]
    public void FromSources_WithOutOfRangeStructureAlpha_Throws()
    {
        Should.Throw<ValidationException>(() => SearchParameters.FromSources(new StubSource(structureAlpha: 1.5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromSources_WithInvalidRrfK_ReportsRrfKProperty(int rrfK)
    {
        var exception = Should.Throw<ValidationException>(() =>
            SearchParameters.FromSources(new StubSource(rrfK: rrfK)));

        exception.Errors.ShouldContain(e => e.PropertyName == "rrfK");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public void FromSources_WithNegativeWeight_ReportsWeightProperty(int weight)
    {
        var exception = Should.Throw<ValidationException>(() =>
            SearchParameters.FromSources(new StubSource(ftsWeight: weight, vectorWeight: 1)));

        exception.Errors.ShouldContain(e => e.PropertyName == "ftsWeight");
    }

    private sealed class StubSource(
        int? rrfK = null,
        int? ftsWeight = null,
        int? vectorWeight = null,
        double? sourceLambda = null,
        double? consolidationThreshold = null,
        DocScoreFormula? docScoreFormula = null,
        CandidateWindowMode? candidateWindow = null,
        double? structureAlpha = null,
        bool? fusionNoRegressionEnabled = null) : ISearchParametersSource
    {
        public int? RrfK { get; } = rrfK;
        public int? FtsWeight { get; } = ftsWeight;
        public int? VectorWeight { get; } = vectorWeight;
        public double? SourceLambda { get; } = sourceLambda;
        public double? ConsolidationThreshold { get; } = consolidationThreshold;
        public DocScoreFormula? DocScoreFormula { get; } = docScoreFormula;
        public CandidateWindowMode? CandidateWindow { get; } = candidateWindow;
        public double? StructureAlpha { get; } = structureAlpha;
        public bool? FusionNoRegressionEnabled { get; } = fusionNoRegressionEnabled;
    }
}
