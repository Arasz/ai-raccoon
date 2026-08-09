using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.search;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FtsQueryNormalizerTests
{
    [Fact]
    public void BuildPlan_RemovesStopwords_LeavingContentTokens()
    {
        // "what"/"is"/"about" are stopwords; the identifier survives as adr + 0070.
        var plan = FtsQueryNormalizer.BuildPlan("What is ADR-0070 about?");
        plan.Expression.ShouldBe("adr AND 0070");
        plan.Fallback.ShouldBe("what OR is OR adr OR 0070 OR about");
    }

    [Fact]
    public void BuildPlan_StopwordOnlyQuery_ReturnsEmptyPlan()
    {
        var plan = FtsQueryNormalizer.BuildPlan("What is the about?");
        plan.Expression.ShouldBe("");
        plan.Fallback.ShouldBeNull();
    }

    [Fact]
    public void BuildPlan_JoinsUpToFourTokensWithAnd_ForPrecision()
    {
        var plan = FtsQueryNormalizer.BuildPlan("ADR governs UUID choice");
        plan.Expression.ShouldBe("adr AND governs AND uuid AND choice");
        plan.Fallback.ShouldNotBeNull();
        plan.TokenCount.ShouldBe(4);
    }

    [Fact]
    public void BuildPlan_OrFallback_IncludesQuotedBigrams_WhenThreeOrMoreTokens()
    {
        var plan = FtsQueryNormalizer.BuildPlan("shadcn ui chosen");
        plan.Fallback.ShouldBe("shadcn OR ui OR chosen OR \"shadcn ui\" OR \"ui chosen\"");
    }

    [Fact]
    public void BuildPlan_AndPrimary_SkipsBigrams_UnderAndSemantics()
    {
        // Bigrams add no constraint under AND; they are reserved for the OR fallback.
        var plan = FtsQueryNormalizer.BuildPlan("shadcn ui chosen");
        plan.Expression.ShouldBe("shadcn AND ui AND chosen");
    }

    [Fact]
    public void BuildPlan_OrFallback_HasNoBigrams_ForTwoTokenQueries()
    {
        var plan = FtsQueryNormalizer.BuildPlan("ADR-0070");
        plan.Fallback.ShouldBe("adr OR 0070");
    }

    [Fact]
    public void BuildPlan_MoreThanFourTokens_KeepsProvenOrRecall_NoFallback()
    {
        // Long queries keep the pre-Wave-1 OR join verbatim: stopword stripping and bigrams
        // regress rankings here (docs/work/archive/2026-08-04-comparison-clean.md).
        var plan = FtsQueryNormalizer.BuildPlan("SQLite memory stores project knowledge");
        plan.Expression.ShouldBe("sqlite OR memory OR stores OR project OR knowledge");
        plan.Fallback.ShouldBeNull();
        plan.TokenCount.ShouldBe(5);
    }

    [Fact]
    public void BuildPlan_LongQueryOrPrimary_RetainsStopwords()
    {
        // 'was' carries past-tense content in "Why was X chosen?" — dropping it drops the
        // expected chunk from fused rank 1 to 2 (docs/work/archive/2026-08-04-comparison-clean.md).
        var plan = FtsQueryNormalizer.BuildPlan("Why was shadcn/ui chosen over gluestack.io?");
        plan.Expression.ShouldBe("why OR was OR shadcn OR ui OR chosen OR over OR gluestack OR io");
        plan.Fallback.ShouldBeNull();
    }

    [Theory]
    [InlineData("alpha AND beta", "alpha AND beta")]
    [InlineData("alpha OR beta", "alpha AND beta")]
    [InlineData("alpha NOT beta", "alpha AND beta")]
    [InlineData("NEAR(alpha beta)", "alpha AND beta")]
    public void BuildPlan_DropsFts5ReservedWords(string query, string expectedExpression)
    {
        var plan = FtsQueryNormalizer.BuildPlan(query);
        plan.Expression.ShouldBe(expectedExpression);
    }

    [Fact]
    public void BuildPlan_StripsPunctuationThatWouldBreakTheFts5Grammar()
    {
        // Quotes, colons, parens, dashes and ellipsis must never reach the MATCH grammar;
        // every generated term comes from the token regex [\p{L}\p{N}_]+.
        var plan = FtsQueryNormalizer.BuildPlan("What is \"ADR-0070\" about? (docs) — …");
        plan.Expression.ShouldBe("adr AND 0070 AND docs");
        plan.Fallback.ShouldBe("what OR is OR adr OR 0070 OR about OR docs OR \"adr 0070\" OR \"0070 docs\"");
    }

    [Fact]
    public void BuildPlan_OrFallback_RetainsQueryStopwords_ForRecall()
    {
        // The fallback is a recall net: the AND primary is precision-only (stopwords stripped),
        // but the OR retry keeps the query's stopwords in query order, falling back to the
        // proven pre-Wave-1 OR behavior (docs/work/archive/2026-08-04-comparison-clean.md).
        var plan = FtsQueryNormalizer.BuildPlan("How does the project handle data erasure?");
        plan.Expression.ShouldBe("project AND handle AND data AND erasure");
        plan.Fallback.ShouldBe(
            "how OR does OR the OR project OR handle OR data OR erasure OR " +
            "\"project handle\" OR \"handle data\" OR \"data erasure\"");
    }

    [Fact]
    public void BuildPlan_MixedCaseQuery_LowercasesBeforeFiltering()
    {
        // Tokens are lowercased before the Reserved/Stopwords membership checks, so casing never
        // reaches the sets — this is the contract that lets the sets use Ordinal comparison
        // (docs/work/archive/2026-08-04-searchvalues-vs-hashset.md).
        var plan = FtsQueryNormalizer.BuildPlan("WHAT IS ADR-0070 ABOUT?");
        plan.Expression.ShouldBe("adr AND 0070");
    }

    [Fact]
    public void BuildPlan_EmptyOrPunctuationOnly_ReturnsEmptyPlan()
    {
        FtsQueryNormalizer.BuildPlan("").Expression.ShouldBe("");
        FtsQueryNormalizer.BuildPlan("   ").Expression.ShouldBe("");
        FtsQueryNormalizer.BuildPlan("?!—…").Expression.ShouldBe("");
    }

    [Fact]
    public void BuildPlan_SingleToken_HasNoFallback()
    {
        var plan = FtsQueryNormalizer.BuildPlan("TDD");
        plan.Expression.ShouldBe("tdd");
        plan.Fallback.ShouldBeNull();
        plan.TokenCount.ShouldBe(1);
    }
}
