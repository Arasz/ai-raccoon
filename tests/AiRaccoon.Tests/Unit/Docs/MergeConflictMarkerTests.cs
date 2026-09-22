using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Docs;

/// <summary>
///     A committed conflict marker is a resolution someone forgot to finish; once shipped it is a
///     decoy for the next resolver of that file. F1/OPS-17 found one in the agent contract for four
///     weeks because nothing looked. The scan reads the tracked-file set from git and greps those
///     files' content, so an untracked draft or an ignored artifact cannot red the gate — and only
///     tracked content is ever pushed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MergeConflictMarkerTests
{
    /// <summary>All three arms, anchored: a `=======` of any other length is prose, not a marker.</summary>
    private const string MarkerPattern = @"^(<<<<<<<|=======$|>>>>>>>)";

    [Fact]
    public async Task NoTrackedFile_ContainsAMergeConflictMarker()
    {
        var root = RepoRoot();
        var tracked = await GitAsync(root, TestContext.Current.CancellationToken, "ls-files");
        tracked.ExitCode.ShouldBe(0, tracked.Stderr);
        tracked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            .ShouldBeGreaterThan(100, "git ls-files returned too little to be the repo");

        var matches = await GitAsync(root, TestContext.Current.CancellationToken,
            "grep", "-n", "-I", "-E", MarkerPattern);

        matches.ExitCode.ShouldBeLessThan(2, $"git grep failed: {matches.Stderr}");
        matches.ExitCode.ShouldBe(1,
            "these tracked files still carry an unresolved merge-conflict marker: " + matches.Stdout.Trim());
    }

    /// <summary>
    ///     The fixture proves the exact scan can see a marker at all: a command that always exits 1
    ///     is indistinguishable from a repo that has nothing (prove-the-check-fails). All three arms
    ///     are planted, since dropping one from the pattern is exactly how this gate would rot.
    /// </summary>
    [Fact]
    public async Task TheScan_SeesAPlantedMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-marker-fixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            (await GitAsync(root, ct, "init", "-q")).ExitCode.ShouldBe(0);
            await File.WriteAllTextAsync(Path.Combine(root, "planted.md"),
                "left\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> origin/main\nright\n", ct);
            await File.WriteAllTextAsync(Path.Combine(root, "clean.md"), "no markers here\n", ct);
            (await GitAsync(root, ct, "add", "-A")).ExitCode.ShouldBe(0);

            var matches = await GitAsync(root, ct, "grep", "-n", "-I", "-E", MarkerPattern);

            matches.ExitCode.ShouldBe(0);
            matches.Stdout.ShouldContain("<<<<<<< HEAD");
            matches.Stdout.ShouldContain("=======");
            matches.Stdout.ShouldContain(">>>>>>> origin/main");
            matches.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3);
            matches.Stdout.ShouldNotContain("clean.md");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<ProcessRun> GitAsync(string root, CancellationToken cancellationToken, params string[] arguments) =>
        RaccoonProcess.RunAsync("git", ["-C", root, .. arguments], TimeSpan.FromSeconds(60), cancellationToken);

    private static string RepoRoot() => Path.GetDirectoryName(TestData.RepoFile("AiRaccoon.slnx"))!;
}
