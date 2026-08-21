using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     `ai-raccoon.ignore` gitignore-subset parsing/matching (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.1, §5.2): `*`, `**`, trailing `/` directory patterns, leading/anchored `/`, no `!`
///     negation, any-match-wins, case per host OS. Pure — no I/O (the file read lives with
///     IIgnoreRulesProvider in Infrastructure).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IgnoreRulesTests
{
    [Fact]
    public void Parse_EmptyContent_HasNoRules()
    {
        var rules = IgnoreRules.Parse("");
        rules.HasRules.ShouldBeFalse();
        rules.IsIgnored("anything.cs", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void IsIgnored_ExactFilePattern_MatchesAtAnyDepth()
    {
        var rules = IgnoreRules.Parse("build.ps1");

        rules.IsIgnored("build.ps1", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("src/build.ps1", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("src/keep.ps1", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void IsIgnored_AnchoredExactFilePattern_MatchesOnlyAtRoot()
    {
        var rules = IgnoreRules.Parse("/src/secret.cs");

        rules.IsIgnored("src/secret.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("nested/src/secret.cs", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void IsIgnored_TrailingSlashDirectoryPattern_IgnoresTheDirectoryAndEverythingBeneath()
    {
        var rules = IgnoreRules.Parse("bin/");

        rules.IsIgnored("bin", isDirectory: true).ShouldBeTrue();
        rules.IsIgnored("bin/x.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("bin/nested/y.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("bins/x.cs", isDirectory: false).ShouldBeFalse();
        // "bin" as a bare file (not a directory) must not be swallowed by a directory-only pattern.
        rules.IsIgnored("bin", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void IsIgnored_StarGlob_MatchesWithinOneSegment()
    {
        var rules = IgnoreRules.Parse("*.generated.cs");

        rules.IsIgnored("src/y.generated.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("src/y.generated.cs.bak", isDirectory: false).ShouldBeFalse();
        rules.IsIgnored("src/sub/deep.generated.cs", isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public void IsIgnored_DoubleStarSegment_MatchesAcrossDirectories()
    {
        var rules = IgnoreRules.Parse("**/node_modules/**");

        rules.IsIgnored("node_modules/pkg/index.js", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("src/node_modules/pkg/index.js", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("src/keep.js", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void IsIgnored_MultiplePatterns_AnyMatchWins()
    {
        var rules = IgnoreRules.Parse("""
                                       bin/
                                       *.generated.cs
                                       build.ps1
                                       """);

        rules.IsIgnored("bin/x.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("src/y.generated.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("build.ps1", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("src/keep.cs", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_CommentsAndBlankLines_AreNotPatterns()
    {
        var rules = IgnoreRules.Parse("""
                                       # comment about bin
                                       bin/

                                       # another comment
                                       """);

        rules.IsIgnored("bin/x.cs", isDirectory: false).ShouldBeTrue();
        // A line that merely mentions "comment" text must never itself become a pattern.
        rules.IsIgnored("comment about bin", isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public void Parse_CrLfContent_NormalizesLineEndings()
    {
        var rules = IgnoreRules.Parse("bin/\r\n*.generated.cs\r\n");

        rules.IsIgnored("bin/x.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("y.generated.cs", isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public void IsIgnored_CaseSensitivity_MatchesHostOsRules()
    {
        var rules = IgnoreRules.Parse("BUILD.ps1");

        rules.IsIgnored("build.ps1", isDirectory: false).ShouldBe(OperatingSystem.IsWindows());
    }

    [Fact]
    public void IsIgnored_NoNegationSupport_BangPrefixIsALiteralUnmatchablePattern()
    {
        // v1: no `!` negation (owner requirement, plan §2.1 / disposition §7.4). A `!` line is
        // parsed as a literal pattern (never matches real content that starts with '!') rather
        // than un-ignoring anything.
        var rules = IgnoreRules.Parse("*.generated.cs\n!keep.generated.cs");

        rules.IsIgnored("keep.generated.cs", isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public void IsIgnored_SiblingDirectoryLookalike_IsNotSwallowedByPrefixMatch()
    {
        var rules = IgnoreRules.Parse("/repo/");

        rules.IsIgnored("repo/x.cs", isDirectory: false).ShouldBeTrue();
        rules.IsIgnored("repo2/x.cs", isDirectory: false).ShouldBeFalse();
    }
}
