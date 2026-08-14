using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Behavioural half of the LikePattern contract: every assertion round-trips the escaped pattern
///     through a real SQLite LIKE ... ESCAPE match. LikePatternTests covers the produced string and
///     that MemorySql declares ESCAPE; this covers that the result actually behaves in the engine.
///     <see cref="LikePattern.Escape" /> is the only thing standing between a path containing a LIKE
///     wildcard and a cross-content cascade delete (<c>MemorySql.DeleteBySourcePath</c>, the same
///     surface that once matched <c>source_file</c> instead of <c>path</c> and deleted manual rows).
///     Every assertion round-trips the escaped pattern through a real SQLite <c>LIKE ... ESCAPE '\'</c>
///     match, not string comparison, so a regression in the engine's own escape semantics would also
///     be caught here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LikePatternSqliteBehaviourTests
{
    [Theory]
    [InlineData("note%off.md", "noteXoff.md")]
    [InlineData("note_1.md", "noteX1.md")]
    [InlineData(@"note\1.md", "noteX1.md")]
    public void Escape_MakesTheWildcardCharacterLiteral_SoOnlyTheExactStringMatches(string literal, string nearMiss)
    {
        Matches(literal, literal).ShouldBeTrue($"the escaped pattern must still match its own literal source ('{literal}')");
        Matches(nearMiss, literal).ShouldBeFalse(
            $"'{nearMiss}' must not match the escaped pattern for '{literal}' — that would mean the wildcard character was treated as a wildcard, not a literal");
    }

    [Fact]
    public void Escape_HandlesAllThreeWildcardCharactersTogether_NotJustOneAtATime()
    {
        // Property intersection: percent, underscore and backslash all present in the same value.
        // A fix that escapes only one of the three (or escapes them in the wrong order, so an
        // earlier substitution's backslash gets re-escaped by a later one) would still pass the
        // single-character cases above but fail here.
        const string literal = @"a_b%c\d.md";

        Matches(literal, literal).ShouldBeTrue();
        Matches("aXb%c\\d.md", literal).ShouldBeFalse("the underscore must stay literal");
        Matches("a_bYc\\d.md", literal).ShouldBeFalse("the percent must stay literal");
        Matches("a_b%cXd.md", literal).ShouldBeFalse("the escaped backslash must not itself become a wildcard escape for the following character");
    }

    [Fact]
    public void Escape_EscapesTheBackslashFirst_SoALiteralBackslashBeforeAWildcardCharDoesNotUnescapeIt()
    {
        // If backslash escaping ran after percent/underscore escaping, a literal "\_" in the input
        // would become "\\_" (escaped backslash followed by an *unescaped* underscore) instead of
        // "\\\_" (escaped backslash followed by an escaped underscore) — silently turning the
        // underscore back into a live wildcard. Order in Escape (backslash, then %, then _) matters.
        const string literal = @"a\_b.md";

        Matches(literal, literal).ShouldBeTrue();
        Matches("a\\Xb.md", literal).ShouldBeFalse("the underscore following a literal backslash must still be escaped, not turned back into a wildcard");
    }

    [Fact]
    public void Escape_OnAPlainStringWithNoWildcardCharacters_IsUnchanged()
    {
        const string literal = "plain-path.md";

        LikePattern.Escape(literal).ShouldBe(literal);
        Matches(literal, literal).ShouldBeTrue();
    }

    /// <summary>True when <paramref name="candidate" /> matches <paramref name="pattern" /> escaped via
    /// <see cref="LikePattern.Escape" />, using a real SQLite engine's LIKE/ESCAPE semantics.</summary>
    private static bool Matches(string candidate, string pattern)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN @candidate LIKE @pattern ESCAPE '\\' THEN 1 ELSE 0 END";
        command.Parameters.AddWithValue("@candidate", candidate);
        command.Parameters.AddWithValue("@pattern", LikePattern.Escape(pattern));
        return (long)command.ExecuteScalar()! == 1;
    }
}
