using AiRaccoon.Core.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>D3 path identity: absolute, separators and ".." resolved, trailing separator stripped, host-OS case.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IngestPathTests
{
    [Fact]
    public void Normalize_RelativePath_ResolvesToAbsolute()
    {
        IngestPath.Normalize(Path.Combine("repo", "..", "file.md")).ShouldBe(Path.GetFullPath("file.md"));
    }

    [Fact]
    public void Normalize_TrailingSeparator_IsStripped()
    {
        var dir = Path.Combine("repo", "sub");
        IngestPath.Normalize(dir + Path.DirectorySeparatorChar).ShouldBe(Path.GetFullPath(dir));
    }

    [Fact]
    public void Normalize_RootPath_KeepsTrailingSeparator()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("."))!;
        IngestPath.Normalize(root).ShouldBe(root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankPath_Throws(string? path)
    {
        Should.Throw<ArgumentException>(() => IngestPath.Normalize(path!));
    }

    [Fact]
    public void PathComparer_FollowsHostOsCaseRules()
    {
        IngestPath.PathComparer.Equals("/Repo/File.md", "/repo/file.md").ShouldBe(OperatingSystem.IsWindows());
    }

    [Fact]
    public void IsWithinScope_DirectoryEntry_CoversItselfAndWholeSubtree()
    {
        var repo = Path.Combine(Path.GetTempPath(), "watch-scope-root", "repo");
        var file = Path.Combine(repo, "src", "Program.cs");

        IngestPath.IsWithinScope(repo, repo).ShouldBeTrue();
        IngestPath.IsWithinScope(Path.Combine(repo, "src"), repo).ShouldBeTrue();
        IngestPath.IsWithinScope(file, repo).ShouldBeTrue();
    }

    [Fact]
    public void IsWithinScope_SiblingAndPrefixLookalike_AreOutside()
    {
        var root = Path.Combine(Path.GetTempPath(), "watch-scope-root");
        var repo = Path.Combine(root, "repo");
        var sibling = Path.Combine(root, "repo-other");

        IngestPath.IsWithinScope(Path.Combine(sibling, "x.md"), repo).ShouldBeFalse();
        IngestPath.IsWithinScope(repo, sibling).ShouldBeFalse();
    }

    [Fact]
    public void IsWithinScope_TrailingSeparatorOnEntry_IsIgnored()
    {
        var repo = Path.Combine(Path.GetTempPath(), "watch-scope-root", "repo");
        IngestPath.IsWithinScope(Path.Combine(repo, "a.md"), repo + Path.DirectorySeparatorChar).ShouldBeTrue();
    }

    [Fact]
    public void IsWithinScope_CaseHandling_MatchesHostOs()
    {
        var root = Path.Combine(Path.GetTempPath(), "watch-scope-root");
        var repo = Path.Combine(root, "repo");
        IngestPath.IsWithinScope(Path.Combine(root, "REPO", "a.md"), repo).ShouldBe(OperatingSystem.IsWindows());
    }

    [Fact]
    public void IsWithinScope_BlankArguments_Throw()
    {
        Should.Throw<ArgumentException>(() => IngestPath.IsWithinScope(" ", "/repo"));
        Should.Throw<ArgumentException>(() => IngestPath.IsWithinScope("/repo", " "));
    }

    [Fact]
    public void HasHiddenOrDeniedSegment_NestedDotDirectory_IsHidden()
    {
        var root = Path.Combine(Path.GetTempPath(), "watch-hidden-root", "repo");
        var file = Path.Combine(root, ".git", "hooks", "pre-commit");

        IngestPath.HasHiddenOrDeniedSegment(root, file, WatchDenySet.Names).ShouldBeTrue();
    }

    [Fact]
    public void HasHiddenOrDeniedSegment_OrdinaryNestedFile_IsNotHidden()
    {
        var root = Path.Combine(Path.GetTempPath(), "watch-hidden-root", "repo");
        var file = Path.Combine(root, "src", "Program.cs");

        IngestPath.HasHiddenOrDeniedSegment(root, file, WatchDenySet.Names).ShouldBeFalse();
    }

    [Fact]
    public void HasHiddenOrDeniedSegment_RootItselfUnderADotAncestor_IsNotHidden()
    {
        // Only segments BELOW the enumeration root count — the root's own ancestry (e.g. a
        // worktree checked out under a dot-prefixed directory) must never disqualify every file.
        var root = Path.Combine(Path.GetTempPath(), ".watch-hidden-dotroot", "repo");
        var file = Path.Combine(root, "src", "Program.cs");

        IngestPath.HasHiddenOrDeniedSegment(root, file, WatchDenySet.Names).ShouldBeFalse();
    }

    [Fact]
    public void HasHiddenOrDeniedSegment_DenySetDirectory_IsDenied()
    {
        var root = Path.Combine(Path.GetTempPath(), "watch-hidden-root", "repo");
        var file = Path.Combine(root, "node_modules", "pkg", "index.js");

        IngestPath.HasHiddenOrDeniedSegment(root, file, WatchDenySet.Names).ShouldBeTrue();
    }

    [Fact]
    public void HasHiddenOrDeniedSegment_DeniedLookalikeName_IsNotDenied()
    {
        var root = Path.Combine(Path.GetTempPath(), "watch-hidden-root", "repo");
        var file = Path.Combine(root, "bins", "x.cs");

        IngestPath.HasHiddenOrDeniedSegment(root, file, WatchDenySet.Names).ShouldBeFalse();
    }

    [Fact]
    public void HasHiddenOrDeniedSegment_HiddenLeafFile_IsHidden()
    {
        var root = Path.Combine(Path.GetTempPath(), "watch-hidden-root", "repo");
        var file = Path.Combine(root, ".env");

        IngestPath.HasHiddenOrDeniedSegment(root, file, WatchDenySet.Names).ShouldBeTrue();
    }
}
