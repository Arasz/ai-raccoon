using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.search;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FtsQueryNormalizerTests
{
    [Fact]
    public void Normalize_JoinsAlphanumericTokensWithOr_ForRecall() =>
        // OR semantics: a doc matching any query token is a candidate; BM25 + RRF re-rank.
        FtsQueryNormalizer.Normalize("SQLite memory stores project knowledge")
            .ShouldBe("sqlite OR memory OR stores OR project OR knowledge");

    [Fact]
    public void Normalize_StripsPunctuationThatWouldBreakTheFts5Grammar() =>
        // A colon would parse as an FTS5 column filter and quotes as a phrase — both must go;
        // the reserved word "or" is dropped with the rest.
        FtsQueryNormalizer.Normalize("What does the project decide or document about: ADR-0001 — Versioning?")
            .ShouldBe("what OR does OR the OR project OR decide OR document OR about OR adr OR 0001 OR versioning");

    [Theory]
    [InlineData("alpha AND beta", "alpha OR beta")]
    [InlineData("alpha OR beta", "alpha OR beta")]
    [InlineData("alpha NOT beta", "alpha OR beta")]
    [InlineData("NEAR(alpha beta)", "alpha OR beta")]
    public void Normalize_DropsFts5ReservedWords(string query, string expected) => FtsQueryNormalizer.Normalize(query).ShouldBe(expected);

    [Fact]
    public void Normalize_EmptyOrPunctuationOnly_ReturnsEmpty()
    {
        FtsQueryNormalizer.Normalize("").ShouldBe("");
        FtsQueryNormalizer.Normalize("?!—…").ShouldBe("");
    }
}
