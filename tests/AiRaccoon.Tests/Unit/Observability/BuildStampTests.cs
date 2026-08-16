using AiRaccoon.Core.Observability;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     <see cref="BuildStamp.Parse" />'s pure parsing contract, decoupled from git and the real
///     build: version/commit split off "version+sha" (docs/plans/2026-08-15-performance-metrics-
///     implementation.md section 7 R2), and the unavailable-stamp contract — commit "unknown",
///     timestamp null, never a silent empty string — when either input is missing.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class BuildStampTests
{
    [Fact]
    public void Parse_InformationalVersionWithSha_SplitsVersionAndCommit()
    {
        var stamp = BuildStamp.Parse("1.15.0+e1f211b0abc123", null);

        stamp.Version.ShouldBe("1.15.0");
        stamp.Commit.ShouldBe("e1f211b0abc123");
    }

    [Fact]
    public void Parse_InformationalVersionWithNoPlus_CommitIsUnknown()
    {
        var stamp = BuildStamp.Parse("1.15.0", null);

        stamp.Version.ShouldBe("1.15.0");
        stamp.Commit.ShouldBe(BuildStamp.UnknownCommit);
    }

    [Fact]
    public void Parse_NullInformationalVersion_VersionEmptyCommitUnknown()
    {
        var stamp = BuildStamp.Parse(null, null);

        stamp.Version.ShouldBe(string.Empty);
        stamp.Commit.ShouldBe(BuildStamp.UnknownCommit);
    }

    [Fact]
    public void Parse_EmptyShaAfterPlus_CommitIsUnknown()
    {
        var stamp = BuildStamp.Parse("1.15.0+", null);

        stamp.Commit.ShouldBe(BuildStamp.UnknownCommit);
    }

    [Fact]
    public void Parse_ValidCommitTimestampText_ParsesToDateTimeOffset()
    {
        var stamp = BuildStamp.Parse("1.15.0+sha", "2026-08-16T08:32:18+02:00");

        stamp.CommitTimestamp.ShouldBe(DateTimeOffset.Parse("2026-08-16T08:32:18+02:00"));
    }

    [Fact]
    public void Parse_MissingCommitTimestampText_CommitTimestampIsNull()
    {
        var stamp = BuildStamp.Parse("1.15.0+sha", null);

        stamp.CommitTimestamp.ShouldBeNull();
    }

    [Fact]
    public void Parse_EmptyCommitTimestampText_CommitTimestampIsNull()
    {
        var stamp = BuildStamp.Parse("1.15.0+sha", string.Empty);

        stamp.CommitTimestamp.ShouldBeNull();
    }

    [Fact]
    public void Parse_MalformedCommitTimestampText_CommitTimestampIsNull()
    {
        var stamp = BuildStamp.Parse("1.15.0+sha", "not-a-timestamp");

        stamp.CommitTimestamp.ShouldBeNull();
    }
}
