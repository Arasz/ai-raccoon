using System.Globalization;
using System.Reflection;
using AiRaccoon.Core.Observability;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     <see cref="AssemblyBuildStamp" /> read off the real built AiRaccoon assembly (same
///     convention as <c>VersionContractTests</c>): version from VERSION, commit sha and commit
///     timestamp from `git log -1` — both independently recomputed here so the assertion is not
///     just echoing what GitStamp.targets already wrote
///     (docs/plans/2026-08-15-performance-metrics-implementation.md section 7 R2, acceptance
///     criteria 1-2). The stamp is only refreshed by an MSBuild pass, so a binary built before
///     HEAD's commit describes an older commit by construction; those comparisons skip, naming the
///     rebuild, rather than report the stale binary as a wrong stamp.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class AssemblyBuildStampTests
{
    private static readonly Assembly Assembly = typeof(MemoryTools).Assembly;

    private static AssemblyBuildStamp Subject() => new(Assembly);

    [Fact]
    public void Version_MatchesTheVersionFile()
    {
        var expected = File.ReadAllText(TestData.RepoFile("VERSION")).Trim();

        Subject().Version.ShouldBe(expected);
    }

    [Fact]
    public async Task Commit_MatchesGitRevParseHead()
    {
        var head = await HeadAsync();
        SkipWhenTheBinaryPredates(head);

        Subject().Commit.ShouldBe(head.Sha, StaleHint(head));
    }

    [Fact]
    public async Task CommitTimestamp_MatchesGitLogCommitterDate()
    {
        var head = await HeadAsync();
        SkipWhenTheBinaryPredates(head);

        Subject().CommitTimestamp.ShouldBe(head.CommittedAt, StaleHint(head));
    }

    [Fact]
    public void CommitTimestampAttribute_IsReadableDirectlyOffTheBinary()
    {
        // Acceptance criterion 1: prove it off the emitted attribute, not the targets file.
        var attribute = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "CommitTimestamp");

        attribute.ShouldNotBeNull("GitStamp.targets must emit a CommitTimestamp AssemblyMetadataAttribute for a normal build");
        DateTimeOffset.TryParse(attribute.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            .ShouldBeTrue($"'{attribute.Value}' must be a parseable timestamp");
    }

    private sealed record Head(string Sha, DateTimeOffset CommittedAt);

    /// <summary>HEAD's sha and committer date, the two values GitStamp.targets and the SDK stamp.</summary>
    private static async Task<Head> HeadAsync()
    {
        var run = await RaccoonProcess.RunAsync(
            "git", ["log", "-1", "--format=%H%n%cI"], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, run.Stderr);

        var lines = run.Stdout.Trim().Split('\n');
        return new Head(lines[0].Trim(),
            DateTimeOffset.Parse(lines[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static DateTimeOffset BuiltAt() => File.GetLastWriteTimeUtc(Assembly.Location);

    private static void SkipWhenTheBinaryPredates(Head head)
    {
        if (BuiltAt() < head.CommittedAt)
        {
            Assert.Skip($"{Assembly.GetName().Name}.dll was built at {BuiltAt():O}, before HEAD {head.Sha} was committed at " +
                        $"{head.CommittedAt:O}; its stamp describes an older commit — run `dotnet build` and rerun");
        }
    }

    private static string StaleHint(Head head) =>
        $"the assembly (built {BuiltAt():O}) should carry HEAD {head.Sha}; if HEAD moved without an MSBuild pass " +
        "(a build the IDE skipped as up to date, or --no-build), run `dotnet build` and rerun";
}
