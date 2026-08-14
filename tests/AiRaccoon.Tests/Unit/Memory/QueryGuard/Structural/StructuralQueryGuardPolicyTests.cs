using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Memory.QueryGuard.Structural;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard.Structural;

/// <summary>
///     <see cref="StructuralQueryGuardPolicy" /> is the third input to the read-path warn tier
///     (docs/adr/0041): a pure score-vs-threshold comparison, no I/O, no embedder — matching
///     <c>QueryGuardPolicy</c>'s own "pure, dependency-free" shape (docs/adr/0040). It never
///     returns Refuse (acceptance criterion 5): the record's false positives on genuine content are
///     not evidence-backed the way the regex refuse tier's are (22/22 graded 2/5).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class StructuralQueryGuardPolicyTests
{
    // scripts/train-structural-noise-model.py, 1% out-of-fold target on the training split.
    private const double SampleThreshold = 0.9961473492522555;

    [Fact]
    public void Evaluate_WithAScoreAtOrAboveTheThreshold_Warns()
    {
        // A real proc_notif excerpt — high-confidence machine-output shape structurally.
        var query =
            "[IMPORTANT: Background process proc_97aa3ea5eb50 completed normally (exit code 0).\nCommand: dotnet test\nOutput:\n]";

        var verdict = StructuralQueryGuardPolicy.Evaluate(query, SampleThreshold);

        verdict.Tier.ShouldBe(QueryGuardTier.Warn);
        verdict.PolicyName.ShouldBe(StructuralQueryGuardPolicy.PolicyName);
        verdict.Guidance.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Evaluate_WithAnOrdinaryQuestion_IsClean()
    {
        var verdict = StructuralQueryGuardPolicy.Evaluate("why did the auth build start failing", SampleThreshold);

        verdict.Tier.ShouldBe(QueryGuardTier.Clean);
    }

    [Fact]
    public void Evaluate_WithNullQuery_Throws()
    {
        Should.Throw<ArgumentNullException>(() => StructuralQueryGuardPolicy.Evaluate(null!, SampleThreshold));
    }

    [Fact]
    public void Evaluate_NeverReturnsRefuse_AcrossTheHeldOutFixture()
    {
        // Acceptance criterion 5, pinned directly: sweep every held-out fixture row (both classes,
        // including the noisiest process-notification content) at a deliberately low threshold —
        // maximally likely to trip a bug that returns Refuse — and assert the tier space is
        // {Clean, Warn} only.
        var path = TestData.RepoFile(
            "tests/AiRaccoon.Tests/Unit/Memory/QueryGuard/Structural/assets/held-out-reproduction-fixture.json");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path)) ?? new Fixture();

        foreach (var row in fixture.Rows)
        {
            var verdict = StructuralQueryGuardPolicy.Evaluate(row.Text, threshold: 0.0);
            verdict.Tier.ShouldNotBe(QueryGuardTier.Refuse, $"fixture '{row.Id}' (family {row.Family})");
        }
    }

    private sealed class Fixture
    {
        [JsonPropertyName("rows")] public List<Row> Rows { get; init; } = [];
    }

    private sealed class Row
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";

        [JsonPropertyName("family")] public string Family { get; init; } = "";

        [JsonPropertyName("text")] public string Text { get; init; } = "";
    }
}
