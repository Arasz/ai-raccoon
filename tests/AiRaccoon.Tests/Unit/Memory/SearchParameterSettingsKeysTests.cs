using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SearchParameterSettingsKeysTests
{
    [Fact]
    public void DefaultConstants_PinADR0006ChosenValues()
    {
        // docs/adr/0006-rrf-parameter-optimization.md: k = 60, 1:1 FTS:vector weights,
        // minScore 0.0, candidate window max(limit x 3, 100). The remaining defaults come
        // from SearchQuery.cs / StructureFusion.cs / FusionConfigKeys.cs. This test exists
        // because nothing else pins these values (reviewer S2, search-parameters plan).
        SearchParameterSettingsKeys.DefaultRrfK.ShouldBe(60);
        SearchParameterSettingsKeys.DefaultFtsWeight.ShouldBe(1);
        SearchParameterSettingsKeys.DefaultVectorWeight.ShouldBe(1);
        SearchParameterSettingsKeys.DefaultSourceLambda.ShouldBe(0.1);
        SearchParameterSettingsKeys.DefaultConsolidationThreshold.ShouldBe(0.1);
        SearchParameterSettingsKeys.DefaultDocScoreFormula.ShouldBe(DocScoreFormula.Max);
        SearchParameterSettingsKeys.DefaultCandidateWindow.ShouldBe(CandidateWindowMode.Max3X100);
        SearchParameterSettingsKeys.DefaultStructureAlpha.ShouldBe(0.5);
        SearchParameterSettingsKeys.DefaultFusionNoRegressionEnabled.ShouldBeFalse();
    }

    [Fact]
    public void SettingsKeys_UseTheRetrievalNamespace()
    {
        SearchParameterSettingsKeys.RrfK.ShouldBe("retrieval.rrfK");
        SearchParameterSettingsKeys.FtsWeight.ShouldBe("retrieval.ftsWeight");
        SearchParameterSettingsKeys.VectorWeight.ShouldBe("retrieval.vectorWeight");
        SearchParameterSettingsKeys.SourceLambda.ShouldBe("retrieval.sourceLambda");
        SearchParameterSettingsKeys.ConsolidationThreshold.ShouldBe("retrieval.consolidationThreshold");
        SearchParameterSettingsKeys.DocScoreFormula.ShouldBe("retrieval.docScoreFormula");
        SearchParameterSettingsKeys.CandidateWindow.ShouldBe("retrieval.candidateWindow");
        SearchParameterSettingsKeys.StructureAlpha.ShouldBe("retrieval.structureAlpha");
        SearchParameterSettingsKeys.FusionNoRegressionEnabled.ShouldBe("fusion.noRegression.enabled.global");
    }

    [Fact]
    public void ParseDocScoreFormula_AcceptsBothWireNames_AndRejectsGarbage()
    {
        SearchParameterSettingsKeys.ParseDocScoreFormula("max").ShouldBe(DocScoreFormula.Max);
        SearchParameterSettingsKeys.ParseDocScoreFormula("sum").ShouldBe(DocScoreFormula.Sum);
        SearchParameterSettingsKeys.ParseDocScoreFormula("MAX").ShouldBe(DocScoreFormula.Max);
        SearchParameterSettingsKeys.ParseDocScoreFormula("m a x").ShouldBeNull();
        SearchParameterSettingsKeys.ParseDocScoreFormula("").ShouldBeNull();
        SearchParameterSettingsKeys.ParseDocScoreFormula(null).ShouldBeNull();
    }

    [Fact]
    public void ParseCandidateWindow_AcceptsBothWireNames_AndRejectsGarbage()
    {
        SearchParameterSettingsKeys.ParseCandidateWindow("max3x100").ShouldBe(CandidateWindowMode.Max3X100);
        SearchParameterSettingsKeys.ParseCandidateWindow("max5x50").ShouldBe(CandidateWindowMode.Max5X50);
        SearchParameterSettingsKeys.ParseCandidateWindow("max3X100").ShouldBe(CandidateWindowMode.Max3X100);
        SearchParameterSettingsKeys.ParseCandidateWindow("100").ShouldBeNull();
        SearchParameterSettingsKeys.ParseCandidateWindow(null).ShouldBeNull();
    }

    [Fact]
    public void ParseNullableInt_RejectsAbsentMalformedAndBelowFloor()
    {
        SearchParameterSettingsKeys.ParseNullableInt("60", 1).ShouldBe(60);
        SearchParameterSettingsKeys.ParseNullableInt("", 1).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableInt("abc", 1).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableInt(null, 1).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableInt("0", 1).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableInt("-5", 0).ShouldBeNull();
        // a zero weight is legitimate (leg disabled) — floor 0 accepts it
        SearchParameterSettingsKeys.ParseNullableInt("0", 0).ShouldBe(0);
    }

    [Fact]
    public void ParseNullableDouble_RejectsAbsentMalformedAndOutOfRange()
    {
        SearchParameterSettingsKeys.ParseNullableDouble("0.3", 0.0, 1.0).ShouldBe(0.3);
        SearchParameterSettingsKeys.ParseNullableDouble("", 0.0, 1.0).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableDouble("abc", 0.0, 1.0).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableDouble(null, 0.0, 1.0).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableDouble("1.5", 0.0, 1.0).ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableDouble("-0.1", 0.0, 1.0).ShouldBeNull();
        // consolidation threshold is unbounded above — only a negative is malformed
        SearchParameterSettingsKeys.ParseNullableDouble("0.05", 0.0, double.MaxValue).ShouldBe(0.05);
        SearchParameterSettingsKeys.ParseNullableDouble("-0.5", 0.0, double.MaxValue).ShouldBeNull();
    }

    [Fact]
    public void ParseNullableBool_AcceptsTrueFalse_AndRejectsGarbage()
    {
        SearchParameterSettingsKeys.ParseNullableBool("true").ShouldBe(true);
        SearchParameterSettingsKeys.ParseNullableBool("TRUE").ShouldBe(true);
        SearchParameterSettingsKeys.ParseNullableBool("false").ShouldBe(false);
        SearchParameterSettingsKeys.ParseNullableBool("1").ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableBool("").ShouldBeNull();
        SearchParameterSettingsKeys.ParseNullableBool(null).ShouldBeNull();
    }
}
