using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FtsQueryNormalizerTests
{
    [Fact]
    public void Normalize_KeepsAlphanumericTokensJoinedBySpaces()
    {
        FtsQueryNormalizer.Normalize("SQLite memory stores project knowledge")
            .ShouldBe("sqlite memory stores project knowledge");
    }

    [Fact]
    public void Normalize_StripsPunctuationThatWouldBreakTheFts5Grammar()
    {
        // A colon would parse as an FTS5 column filter and quotes as a phrase — both must go;
        // the reserved word "or" is dropped with the rest.
        FtsQueryNormalizer.Normalize("What does the project decide or document about: ADR-0001 — Versioning?")
            .ShouldBe("what does the project decide document about adr 0001 versioning");
    }

    [Theory]
    [InlineData("alpha AND beta", "alpha beta")]
    [InlineData("alpha OR beta", "alpha beta")]
    [InlineData("alpha NOT beta", "alpha beta")]
    [InlineData("NEAR(alpha beta)", "alpha beta")]
    public void Normalize_DropsFts5ReservedWords(string query, string expected)
    {
        FtsQueryNormalizer.Normalize(query).ShouldBe(expected);
    }

    [Fact]
    public void Normalize_EmptyOrPunctuationOnly_ReturnsEmpty()
    {
        FtsQueryNormalizer.Normalize("").ShouldBe("");
        FtsQueryNormalizer.Normalize("?!—…").ShouldBe("");
    }
}