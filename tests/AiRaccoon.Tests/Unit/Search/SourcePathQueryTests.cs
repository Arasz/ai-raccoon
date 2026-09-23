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
            "{source_file} : \"docs adr 0011 frontend chassis stack md\" AND {source_file section} : \"decision\"");
    }

    /// <summary>Plain text carries no sections, but the file itself is still an anchor.</summary>
    [Fact]
    public void TryBuild_TxtFileOnly_IsAnAnchor()
    {
        SourcePathQuery.TryBuild("engine/requirements.txt", out var expression).ShouldBeTrue();

        expression.ShouldBe("{source_file} : \"engine requirements txt\"");
    }

    [Fact]
    public void TryBuild_FileOnly_MatchesSourceColumnWithAnd()
    {
        SourcePathQuery.TryBuild("docs/adr/0011-frontend-chassis-stack.md", out var expression)
            .ShouldBeTrue();

        expression.ShouldBe("{source_file} : \"docs adr 0011 frontend chassis stack md\"");
    }

    [Theory]
    [InlineData("What does ADR-0011 decide?")]
    [InlineData("ADR-0070")]
    [InlineData("docs/adr/0011.md#")]
    [InlineData("requirements.txt#install")]
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

        expression.ShouldBe("{source_file} : \"notes md\" AND {source_file section} : \"getting-started\"");
    }

    /// <summary>A section named the way its heading reads, spaces and all, is still an anchor.</summary>
    [Fact]
    public void TryBuild_SectionWithSpaces_IsAnAnchor()
    {
        SourcePathQuery.TryBuild("observatory.md#Coastal duties", out var expression).ShouldBeTrue();

        expression.ShouldBe("{source_file} : \"observatory md\" AND {source_file section} : \"coastal duties\"");
    }

    /// <summary>Inside a quoted phrase an FTS5 operator word is an ordinary token.</summary>
    [Theory]
    [InlineData("docs/and.md#or")]
    [InlineData("docs/AND.md#OR")]
    public void TryBuild_ReservedWordToken_StaysInsideAPhrase(string query)
    {
        SourcePathQuery.TryBuild(query, out var expression).ShouldBeTrue();

        expression.ShouldBe("{source_file} : \"docs and md\" AND {source_file section} : \"or\"");
    }

    [Theory]
    [InlineData("ferry-notes.md", "/docs/ferry-notes.md", true)]
    [InlineData("Ferry-Notes.MD", "/docs/ferry-notes.md", true)]
    [InlineData("docs/ferry-notes.md", "/work/docs/ferry-notes.md", true)]
    [InlineData("/docs/ferry-notes.md", "/docs/ferry-notes.md", true)]
    [InlineData("ferry-notes.md", "/ferry/notes.md", false)]
    [InlineData("ferry-notes.md", "/docs/old-ferry-notes.md", false)]
    [InlineData("docs/ferry-notes.md", "/work/olddocs/ferry-notes.md", false)]
    [InlineData("/docs/ferry-notes.md", "/work/docs/ferry-notes.md", false)]
    [InlineData("ferry-notes.md", null, false)]
    public void NamesFile_MatchesTheTypedNameAtAPathBoundary(string anchorFile, string? sourceFile, bool expected)
    {
        SourcePathQuery.NamesFile(anchorFile, sourceFile).ShouldBe(expected);
    }

    [Theory]
    [InlineData("notes.md#what does it say?")]
    [InlineData("notes.md#two  spaces")]
    [InlineData("notes.md# leading")]
    public void TryBuild_SectionThatIsNotAHeadingShape_IsNotAnAnchor(string query)
    {
        SourcePathQuery.TryBuild(query, out _).ShouldBeFalse();
    }
}
