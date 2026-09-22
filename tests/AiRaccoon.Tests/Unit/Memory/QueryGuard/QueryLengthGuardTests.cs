using AiRaccoon.Core.Memory.QueryGuard;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard;

/// <summary>
///     The caller-visible half of ADR-0071: a query long enough that the bundled model's
///     embedding window will likely trim it gets a Warn verdict, unconditionally.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class QueryLengthGuardTests
{
    [Fact]
    public void Evaluate_WithAShortOrdinaryQuery_IsClean()
    {
        QueryLengthGuard.Evaluate("why did the auth build start failing").Tier.ShouldBe(QueryGuardTier.Clean);
    }

    [Fact]
    public void Evaluate_AtExactlyTheThreshold_IsClean()
    {
        var query = new string('a', QueryLengthGuard.WarnThresholdChars);
        query.Length.ShouldBe(QueryLengthGuard.WarnThresholdChars); // pin the fixture to the boundary before asserting behaviour

        QueryLengthGuard.Evaluate(query).Tier.ShouldBe(QueryGuardTier.Clean);
    }

    [Fact]
    public void Evaluate_OneCharOverTheThreshold_Warns()
    {
        var query = new string('a', QueryLengthGuard.WarnThresholdChars + 1);
        query.Length.ShouldBe(QueryLengthGuard.WarnThresholdChars + 1);

        var verdict = QueryLengthGuard.Evaluate(query);

        verdict.Tier.ShouldBe(QueryGuardTier.Warn);
        verdict.PolicyName.ShouldBe(QueryLengthGuard.WarnPolicyName);
        verdict.Guidance.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Evaluate_WellOverTheThreshold_Warns()
    {
        var query = new string('a', QueryLengthGuard.WarnThresholdChars * 4);

        QueryLengthGuard.Evaluate(query).Tier.ShouldBe(QueryGuardTier.Warn);
    }

    [Fact]
    public void Evaluate_Guidance_SaysKeywordMatchingStillSeesTheWholeQuery()
    {
        // The FTS leg builds its plan from the full query text even though the embedding is
        // trimmed (SqliteMemoryStore.SearchAsync) -- the guidance has to say so, or it misleads
        // the caller into thinking the whole query was dropped rather than just the semantic leg.
        var query = new string('a', QueryLengthGuard.WarnThresholdChars + 1);

        var verdict = QueryLengthGuard.Evaluate(query);

        verdict.Guidance.ShouldNotBeNullOrWhiteSpace();
        verdict.Guidance!.ShouldContain("keyword");
    }

    [Fact]
    public void Evaluate_WithNullQuery_Throws()
    {
        Should.Throw<ArgumentNullException>(() => QueryLengthGuard.Evaluate(null!));
    }

    // pi-badger-integration F1: the caller supplies the ACTIVE engine's real token budget (a
    // manifest model's is wider than the bundled default) instead of the guard assuming 254 always.

    [Fact]
    public void Evaluate_WithAWiderBudget_AQueryOverTheBundledThresholdButUnderTheWiderOne_IsClean()
    {
        // 1500 chars is over the bundled 1,000-char threshold but under a 510-token budget's own
        // (~2,008-char) threshold -- no warning should fire for an engine with that much room.
        var query = new string('a', 1500);

        QueryLengthGuard.Evaluate(query, budgetTokens: 510).Tier.ShouldBe(QueryGuardTier.Clean);
    }

    [Fact]
    public void Evaluate_WithAWiderBudget_AtExactlyItsScaledThreshold_IsClean()
    {
        var query = new string('a', WiderBudgetThresholdChars);

        QueryLengthGuard.Evaluate(query, budgetTokens: 510).Tier.ShouldBe(QueryGuardTier.Clean);
    }

    [Fact]
    public void Evaluate_WithAWiderBudget_OneCharOverItsScaledThreshold_WarnsNamingThatBudget()
    {
        var query = new string('a', WiderBudgetThresholdChars + 1);

        var verdict = QueryLengthGuard.Evaluate(query, budgetTokens: 510);

        verdict.Tier.ShouldBe(QueryGuardTier.Warn);
        verdict.Guidance.ShouldNotBeNullOrWhiteSpace();
        verdict.Guidance!.ShouldContain("510");
    }

    /// <summary>The 510-token budget's own char threshold, scaled from the bundled model's own
    /// tokens-to-chars ratio (1,000 chars / 254 tokens) -- pinned here so the two tests above document
    /// where the boundary actually falls instead of asserting a number chosen to make them pass.</summary>
    private const int WiderBudgetThresholdChars = 2008;
}
