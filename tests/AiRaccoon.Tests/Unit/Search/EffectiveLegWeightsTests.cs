using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     The confidence-weighted RRF wiring seam (issue #706): the point in
///     <see cref="SqliteMemoryStore" /> where <see cref="LegConfidence" /> actually gets applied to
///     a search's configured FTS/vector weights. No embedding engine or bank needed — candidates
///     are synthetic <see cref="MemorySearchResult" /> lists in the same best-first order
///     ModalityCandidates already produces.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EffectiveLegWeightsTests
{
    private static MemorySearchResult Candidate(string hash, double ranking) => new(hash, ranking, $"{hash}.md", "snippet");

    private static SearchParameters ParametersWith(bool legConfidenceEnabled, int ftsWeight = 1, int vectorWeight = 1) =>
        SearchParameters.FromSources(new StubSource(ftsWeight, vectorWeight, legConfidenceEnabled));

    /// <summary>The default: never change existing behaviour when the flag is off.</summary>
    [Fact]
    public void EffectiveLegWeights_FlagOff_ReturnsTheConfiguredWeightsUnscaled()
    {
        var parameters = ParametersWith(legConfidenceEnabled: false, ftsWeight: 3, vectorWeight: 2);
        var fts = new[] { Candidate("a", -40.0), Candidate("b", 0.0) };
        var vector = new[] { Candidate("c", 0.9), Candidate("d", 0.9) };

        var (ftsWeight, vectorWeight) = SqliteMemoryStore.EffectiveLegWeights(parameters, fts, vector);

        ftsWeight.ShouldBe(3.0);
        vectorWeight.ShouldBe(2.0);
    }

    /// <summary>
    ///     Flag on, one decisive leg (a huge rank-1..rank-k separation) and one flat leg: the
    ///     decisive leg's weight must scale strictly above the flat leg's, in the same direction
    ///     <see cref="LegConfidence" /> is specified to move them.
    /// </summary>
    [Fact]
    public void EffectiveLegWeights_FlagOn_ScalesTheDecisiveLegAboveTheFlatLeg()
    {
        var parameters = ParametersWith(legConfidenceEnabled: true, ftsWeight: 1, vectorWeight: 1);
        var decisiveFts = new[] { Candidate("a", -40.0), Candidate("b", -1.0), Candidate("c", -1.0), Candidate("d", -1.0), Candidate("e", -1.0) };
        var flatVector = new[] { Candidate("f", 0.9), Candidate("g", 0.9), Candidate("h", 0.9), Candidate("i", 0.9), Candidate("j", 0.9) };

        var (ftsWeight, vectorWeight) = SqliteMemoryStore.EffectiveLegWeights(parameters, decisiveFts, flatVector);

        ftsWeight.ShouldBeGreaterThan(vectorWeight);
        vectorWeight.ShouldBe(1.0 * LegConfidence.MinWeight, 0.0001);
    }

    /// <summary>Flag on but a leg has no candidates (skipped/degraded): its weight passes through unscaled, not zeroed or thrown.</summary>
    [Fact]
    public void EffectiveLegWeights_FlagOn_EmptyLeg_ReturnsItsBaseWeightUnscaled()
    {
        var parameters = ParametersWith(legConfidenceEnabled: true, ftsWeight: 1, vectorWeight: 0);
        var fts = new[] { Candidate("a", -5.0), Candidate("b", -1.0) };

        var (_, vectorWeight) = SqliteMemoryStore.EffectiveLegWeights(parameters, fts, []);

        vectorWeight.ShouldBe(0.0);
    }

    private sealed class StubSource(int ftsWeight, int vectorWeight, bool legConfidenceEnabled) : ISearchParametersSource
    {
        public int? RrfK => null;
        public int? FtsWeight => ftsWeight;
        public int? VectorWeight => vectorWeight;
        public double? SourceLambda => null;
        public double? ConsolidationThreshold => null;
        public DocScoreFormula? DocScoreFormula => null;
        public CandidateWindowMode? CandidateWindow => null;
        public double? StructureAlpha => null;
        public bool? FusionNoRegressionEnabled => null;
        public bool? LegConfidenceEnabled => legConfidenceEnabled;
    }
}
