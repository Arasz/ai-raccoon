using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Tests;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     Package E1: the steady-state choke map is a cached <see cref="ProjectIdAliasMap" /> backed by
///     the durable table. <see cref="ProjectIdAliasMap.Default" /> starts empty and stays empty
///     until a map change reloads it; every choke (ToolGate, key helpers, watch boundaries, sync)
///     reads it through <c>Default.Fold</c>, so an empty map must pass ids through byte-identical.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ProjectIdAliasMapDefaultTests
{
    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry("old-slug", "new-slug")],
        ["new-slug"],
        ["qa-noise-project"]);

    [Fact]
    public void DefaultReplaceReset_RoundTripsTheLoadedMap()
    {
        try
        {
            ProjectIdAliasMap.Default.IsEmpty.ShouldBeTrue("steady state ships empty (ADR-0099)");

            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            ProjectIdAliasMap.Default.Fold("old-slug").ShouldBe("new-slug");
            ProjectIdAliasMap.Default.IsDropped("qa-noise-project").ShouldBeTrue();
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }

        ProjectIdAliasMap.Default.IsEmpty.ShouldBeTrue("reset restores the empty steady state");
    }

    [Fact]
    public void EmptyMap_PassesEverySpellingThroughByteIdentical()
    {
        // E-AC3 regression: with the table empty the chokes must behave exactly as before the
        // durable map existed. No normalization beyond the pre-existing guid D-form rule.
        var ids = new[]
        {
            "job-search-ai-assistant", "acme", "OLD-ID", "old-id",
            "8F6E4281-80C7-4E9A-9E9A-5B9A6A9A6A9A", "{bracketed}", "  padded  ",
        };

        foreach (var id in ids)
        {
            ProjectIdAliasMap.Empty.Fold(id).ShouldBe(ProjectId.Canonicalize(id), $"id '{id}'");
            ProjectIdAliasMap.Empty.IsDropped(id).ShouldBeFalse($"id '{id}'");
        }
    }

    [Fact]
    public void KeyHelpers_InheritTheLoadedMapThroughDefaultFold()
    {
        // E1: IngestScopeKeys / AccessModePolicy / WatchConfigKeys already call Default.Fold at
        // construction — no helper edits, proven by loading Default and reading the keys back.
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            IngestScopeKeys.ScopeProject("old-slug").ShouldBe(IngestScopeKeys.ScopeProject("new-slug"));
            AccessModePolicy.ProjectSettingKey("old-slug")
                .ShouldBe(AccessModePolicy.ProjectSettingKey("new-slug"));
            WatchConfigKeys.EnabledProject("old-slug").ShouldBe(WatchConfigKeys.EnabledProject("new-slug"));
            WatchConfigKeys.ConcurrencyProject("old-slug")
                .ShouldBe(WatchConfigKeys.ConcurrencyProject("new-slug"));
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    [Fact]
    public void KeyHelpers_WithEmptyDefault_KeepTheRawSpelling()
    {
        // The other half of E-AC3: an empty Default leaves key construction exactly as today.
        IngestScopeKeys.ScopeProject("old-slug").ShouldBe("ingest.scope.old-slug");
        AccessModePolicy.ProjectSettingKey("old-slug").ShouldBe("access.mode.project:old-slug");
        WatchConfigKeys.EnabledProject("old-slug").ShouldBe("watch.enabled.old-slug");
    }
}
