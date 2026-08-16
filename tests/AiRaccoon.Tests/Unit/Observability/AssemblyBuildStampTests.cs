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
///     convention as <c>VersionContractTests</c>): version from VERSION, commit sha from
///     `git rev-parse HEAD`, commit timestamp from `git log -1 --format=%cI` — both independently
///     recomputed here so the assertion is not just echoing what GitStamp.targets already wrote
///     (docs/plans/2026-08-15-performance-metrics-implementation.md section 7 R2, acceptance
///     criteria 1-2).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class AssemblyBuildStampTests
{
    private static AssemblyBuildStamp Subject() => new(typeof(MemoryTools).Assembly);

    [Fact]
    public void Version_MatchesTheVersionFile()
    {
        var expected = File.ReadAllText(TestData.RepoFile("VERSION")).Trim();

        Subject().Version.ShouldBe(expected);
    }

    [Fact]
    public async Task Commit_MatchesGitRevParseHead()
    {
        var run = await RaccoonProcess.RunAsync(
            "git", ["rev-parse", "HEAD"], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, run.Stderr);

        Subject().Commit.ShouldBe(run.Stdout.Trim());
    }

    [Fact]
    public async Task CommitTimestamp_MatchesGitLogCommitterDate()
    {
        var run = await RaccoonProcess.RunAsync(
            "git", ["log", "-1", "--format=%cI"], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, run.Stderr);
        var expected = DateTimeOffset.Parse(run.Stdout.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Subject().CommitTimestamp.ShouldBe(expected);
    }

    [Fact]
    public void CommitTimestampAttribute_IsReadableDirectlyOffTheBinary()
    {
        // Acceptance criterion 1: prove it off the emitted attribute, not the targets file.
        var attribute = typeof(MemoryTools).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "CommitTimestamp");

        attribute.ShouldNotBeNull("GitStamp.targets must emit a CommitTimestamp AssemblyMetadataAttribute for a normal build");
        DateTimeOffset.TryParse(attribute.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            .ShouldBeTrue($"'{attribute.Value}' must be a parseable timestamp");
    }
}
