using Shouldly;
using Xunit;
using SourcePathQuery = AiRaccoon.Infrastructure.Sqlite.Memory.SourcePathQuery;

namespace AiRaccoon.Tests.Unit.Search;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SourcePathQueryTests
{
    [Fact]
    public void TryBuild_FileWithSection_MatchesSourceAndSectionColumnsWithAnd()
    {
        SourcePathQuery.TryBuild("docs/adr/0011-frontend-chassis-stack.md#decision", out var expression)
            .ShouldBeTrue();

        expression.ShouldBe(
            "{source_file section} : (docs AND adr AND 0011 AND frontend AND chassis AND stack AND md AND \"decision\")");
    }

    [Fact]
    public void TryBuild_FileOnly_MatchesSourceColumnWithAnd()
    {
        SourcePathQuery.TryBuild("docs/adr/0011-frontend-chassis-stack.md", out var expression)
            .ShouldBeTrue();

        expression.ShouldBe("{source_file} : (docs AND adr AND 0011 AND frontend AND chassis AND stack AND md)");
    }

    [Theory]
    [InlineData("What does ADR-0011 decide?")]
    [InlineData("ADR-0070")]
    [InlineData("docs/adr/0011.md#")]
    [InlineData("")]
    public void TryBuild_NonPathShapes_ReturnFalse(string query)
    {
        SourcePathQuery.TryBuild(query, out var expression).ShouldBeFalse();
        expression.ShouldBeEmpty();
    }

    /// <summary>An FTS5 bareword cannot contain a hyphen, and markdown anchors routinely do.</summary>
    [Fact]
    public void TryBuild_HyphenatedSection_IsQuoted()
    {
        SourcePathQuery.TryBuild("notes.md#getting-started", out var expression).ShouldBeTrue();

        expression.ShouldBe("{source_file section} : (notes AND md AND \"getting-started\")");
    }

    /// <summary>A section named the way its heading reads, spaces and all, is still an anchor.</summary>
    [Fact(Skip = "Defect: PathRegex's section group is [\\w-]+, so a section typed with spaces is not an anchor query at all.")]
    public void TryBuild_SectionWithSpaces_IsAnAnchor()
    {
        SourcePathQuery.TryBuild("observatory.md#Coastal duties", out var expression).ShouldBeTrue();

        expression.ShouldBe("{source_file section} : (observatory AND md AND \"coastal duties\")");
    }

    [Fact]
    public void TryBuild_ReservedWordToken_IsQuoted()
    {
        SourcePathQuery.TryBuild("docs/and.md#or", out var expression).ShouldBeTrue();

        expression.ShouldBe("{source_file section} : (docs AND \"and\" AND md AND \"or\")");
    }

    [Fact]
    public void TryBuild_MixedCaseReservedWord_IsQuoted()
    {
        // Tokens are lowercased before the Reserved check, so casing never reaches the
        // set — the contract that lets it use Ordinal comparison.
        SourcePathQuery.TryBuild("docs/AND.md#OR", out var expression).ShouldBeTrue();

        expression.ShouldBe("{source_file section} : (docs AND \"and\" AND md AND \"or\")");
    }
}
