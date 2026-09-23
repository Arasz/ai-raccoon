using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>(projectId, path) identity: ordinal project id, IngestPath.PathComparer for path.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchKeyTests
{
    [Fact]
    public void Equals_SameProjectIdAndPath_IsTrue()
    {
        var a = new WatchKey("acme", "/repo/a.md");
        var b = new WatchKey("acme", "/repo/a.md");

        a.Equals(b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void Equals_PathsDifferingOnlyInCase_MatchesPathComparer()
    {
        var a = new WatchKey("acme", "/repo/A.md");
        var b = new WatchKey("acme", "/repo/a.md");
        var expected = IngestPath.PathComparer.Equals("/repo/A.md", "/repo/a.md");

        a.Equals(b).ShouldBe(expected);
        if (expected)
        {
            a.GetHashCode().ShouldBe(b.GetHashCode());
        }
    }

    [Fact]
    public void Equals_ProjectIdsDifferingOnlyInCase_IsNeverTrue()
    {
        var a = new WatchKey("acme", "/repo/a.md");
        var b = new WatchKey("ACME", "/repo/a.md");

        a.Equals(b).ShouldBeFalse();
        (a == b).ShouldBeFalse();
    }

    [Fact]
    public void HashSet_TwoPathSpellings_HoldsOneEntryExactlyWhenPathComparerSaysEqual()
    {
        const string pathA = "/repo/B.md";
        const string pathB = "/repo/b.md";
        var expectedCount = IngestPath.PathComparer.Equals(pathA, pathB) ? 1 : 2;

        var set = new HashSet<WatchKey> { new("acme", pathA), new("acme", pathB) };

        set.Count.ShouldBe(expectedCount);
    }
}
