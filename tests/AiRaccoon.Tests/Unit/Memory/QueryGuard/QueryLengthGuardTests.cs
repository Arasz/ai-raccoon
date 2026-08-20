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
}
