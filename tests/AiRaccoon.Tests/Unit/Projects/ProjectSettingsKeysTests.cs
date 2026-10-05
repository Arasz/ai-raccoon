using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     The per-project settings prefixes are one list, read by the settings endpoint, the census and
///     the repair: a key one of them attributes to a project, the others attribute to the same one.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ProjectSettingsKeysTests
{
    private const string Id = "0199a1b2-0000-7000-8000-0000000000c1";

    public static TheoryData<string> EveryPrefix => new()
    {
        "ingest.scope.",
        "watch.scope.",
        "watch.enabled.",
        "watch.concurrency.",
        "access.mode.project:"
    };

    /// <summary>Census attribution before the list moved to Core: key, then the owner (null when unattributed).</summary>
    public static TheoryData<string, string?> CensusAttribution => new()
    {
        { $"ingest.scope.{Id}", Id },
        { $"watch.scope.{Id}", Id },
        { $"watch.enabled.{Id}", Id },
        { $"watch.concurrency.{Id}", Id },
        { $"access.mode.project:{Id}", Id },
        { "ingest.scope.global", null },
        { "watch.enabled.global", null },
        { "watch.concurrency.global", null },
        { "watch.scope.global", "global" },
        { "access.mode.project:global", "global" },
        { "access.mode.global", null },
        { "ingest.scope.", null },
        { "access.mode.project:", null },
        { "watch.enabled.globalx", "globalx" },
        { "sweep.threshold", null },
        { "queryGuard.enabled.global", null }
    };

    [Theory]
    [MemberData(nameof(EveryPrefix))]
    public void TryGetProjectId_EachPrefix_ReturnsTheOwner(string prefix)
    {
        ProjectSettingsKeys.TryGetProjectId($"{prefix}{Id}", out var owner).ShouldBeTrue();

        owner.ShouldBe(Id);
    }

    [Fact]
    public void Prefixes_AreExactlyTheFivePerProjectPrefixes()
    {
        ProjectSettingsKeys.Prefixes.Select(p => p.Prefix).ShouldBe(EveryPrefix.Select(row => row.Data));
    }

    [Theory]
    [InlineData("ingest.scope.global")]
    [InlineData("watch.enabled.global")]
    [InlineData("watch.concurrency.global")]
    public void TryGetProjectId_GlobalExcludedWhereItWasBefore(string key)
    {
        ProjectSettingsKeys.TryGetProjectId(key, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("watch.scope.global")]
    [InlineData("access.mode.project:global")]
    public void TryGetProjectId_GlobalIsAProjectWhereItWasBefore(string key)
    {
        ProjectSettingsKeys.TryGetProjectId(key, out var owner).ShouldBeTrue();

        owner.ShouldBe("global");
    }

    [Theory]
    [InlineData("access.mode.project:")]
    [InlineData("ingest.scope.")]
    [InlineData("watch.enabled. ")]
    public void TryGetProjectId_BlankOwner_IsAProjectKeyWithABlankOwner(string key)
    {
        ProjectSettingsKeys.TryGetProjectId(key, out var owner).ShouldBeTrue();

        string.IsNullOrWhiteSpace(owner).ShouldBeTrue();
    }

    [Theory]
    [InlineData("access.mode.global")]
    [InlineData("sweep.threshold")]
    [InlineData("ingest.scopex")]
    public void TryGetProjectId_NonProjectKey_IsFalse(string key)
    {
        ProjectSettingsKeys.TryGetProjectId(key, out _).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(CensusAttribution))]
    public void TheCensus_AttributesExactlyTheKeysItDidBefore(string key, string? expectedOwner)
    {
        var attributed = ProjectIdCensus.TryAttributeSetting(key, out var owner);

        attributed.ShouldBe(expectedOwner is not null);
        owner.ShouldBe(expectedOwner);
    }

    [Fact]
    public void KeysFor_MatchesTheCensusAttribution()
    {
        var keys = ProjectSettingsKeys.KeysFor(Id);

        keys.ShouldBe(
        [
            $"ingest.scope.{Id}",
            $"watch.scope.{Id}",
            $"watch.enabled.{Id}",
            $"watch.concurrency.{Id}",
            $"access.mode.project:{Id}"
        ]);
        foreach (var key in keys)
        {
            ProjectIdCensus.TryAttributeSetting(key, out var owner).ShouldBeTrue(key);
            owner.ShouldBe(Id, key);
        }
    }

    [Fact]
    public void KeysFor_Global_ContainsOnlyProjectAttributedKeys()
    {
        ProjectSettingsKeys.KeysFor("global").ShouldBe(["watch.scope.global", "access.mode.project:global"]);
    }

    [Theory]
    [InlineData("ingest.scope.")]
    [InlineData("watch.enabled.")]
    [InlineData("watch.concurrency.")]
    public void WithProjectId_RejectsMachineGlobalSourceAndTarget(string prefix)
    {
        Should.Throw<ArgumentException>(() => ProjectSettingsKeys.WithProjectId(prefix + "global", Id));
        Should.Throw<ArgumentException>(() => ProjectSettingsKeys.WithProjectId(prefix + Id, "global"));
    }

    [Theory]
    [MemberData(nameof(EveryPrefix))]
    public void WithProjectId_ReplacesOnlyTheOwner(string prefix)
    {
        ProjectSettingsKeys.WithProjectId($"{prefix}{{0199A1B2-0000-7000-8000-0000000000C1}}", Id).ShouldBe($"{prefix}{Id}");
    }

    [Fact]
    public void WithProjectId_OnANonProjectKey_Throws()
    {
        Should.Throw<ArgumentException>(() => ProjectSettingsKeys.WithProjectId("sweep.threshold", Id));
    }
}
