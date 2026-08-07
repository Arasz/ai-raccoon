using AiRaccoon.Core.Ingestion;
using System.Text.Json;
using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Settings keys (contract with the CLI task) and per-project-over-global resolution.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchConfigTests
{
    private static WatchConfig Resolve(Dictionary<string, string?> settings, string projectId = "acme") =>
        WatchConfig.Resolve(projectId, key => settings.GetValueOrDefault(key));

    private static string ScopePath(string name) => Path.Combine(Path.GetTempPath(), "watch-config", name);

    [Fact]
    public void Keys_ExactStrings_AreTheClIWrittenContract()
    {
        WatchConfigKeys.EnabledGlobal.ShouldBe("watch.enabled.global");
        IngestScopeKeys.ScopeGlobal.ShouldBe("ingest.scope.global");
        WatchConfigKeys.ConcurrencyGlobal.ShouldBe("watch.concurrency.global");
        WatchConfigKeys.EnabledProject("acme").ShouldBe("watch.enabled.acme");
        IngestScopeKeys.ScopeProject("acme").ShouldBe("ingest.scope.acme");
        WatchConfigKeys.ConcurrencyProject("acme").ShouldBe("watch.concurrency.acme");
    }

    [Fact]
    public void Resolve_NothingSet_DisabledWithEmptyScopeAndConcurrency4()
    {
        var config = Resolve([]);
        config.Enabled.ShouldBeFalse();
        config.Scope.ShouldBeEmpty();
        config.Concurrency.ShouldBe(4);
    }

    [Fact]
    public void Resolve_GlobalEnabled_AppliesToProject()
    {
        var config = Resolve(new Dictionary<string, string?> { [WatchConfigKeys.EnabledGlobal] = "true" });
        config.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_EnabledValue_IsParsedCaseInsensitively()
    {
        var config = Resolve(new Dictionary<string, string?> { [WatchConfigKeys.EnabledGlobal] = "TRUE" });
        config.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_ProjectEntry_BeatsGlobal()
    {
        var settings = new Dictionary<string, string?>
        {
            [WatchConfigKeys.EnabledGlobal] = "true",
            [WatchConfigKeys.EnabledProject("acme")] = "false"
        };
        Resolve(settings).Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_Scope_GlobalListApplies_WhenNoProjectList()
    {
        var global = ScopePath("global");
        var config = Resolve(new Dictionary<string, string?> { [IngestScopeKeys.ScopeGlobal] = IngestScopeKeys.Serialize([global]) });
        config.Scope.ShouldBe([global]);
    }

    [Fact]
    public void Resolve_Scope_ProjectListBeatsGlobal()
    {
        var settings = new Dictionary<string, string?>
        {
            [IngestScopeKeys.ScopeGlobal] = IngestScopeKeys.Serialize([ScopePath("global")]),
            [IngestScopeKeys.ScopeProject("acme")] = IngestScopeKeys.Serialize([ScopePath("project")])
        };
        Resolve(settings).Scope.ShouldBe([ScopePath("project")]);
    }

    [Fact]
    public void Resolve_Scope_ProjectEmptyListBeatsGlobal()
    {
        var settings = new Dictionary<string, string?>
        {
            [IngestScopeKeys.ScopeGlobal] = IngestScopeKeys.Serialize([ScopePath("global")]),
            [IngestScopeKeys.ScopeProject("acme")] = "[]"
        };
        Resolve(settings).Scope.ShouldBeEmpty();
    }

    [Fact]
    public void SerializeScope_RoundTripsThroughJsonArray()
    {
        var paths = new[] { ScopePath("a"), ScopePath("b") };
        JsonSerializer.Deserialize<string[]>(IngestScopeKeys.Serialize(paths)).ShouldBe(paths);
    }

    [Fact]
    public void SerializeScope_NormalizesEntries()
    {
        var path = ScopePath("a");
        JsonSerializer.Deserialize<string[]>(IngestScopeKeys.Serialize([path + Path.DirectorySeparatorChar]))
            .ShouldBe([path]);
    }

    [Fact]
    public void ParseScope_NullOrBlank_IsAbsent()
    {
        IngestScopeKeys.Parse(null).ShouldBeNull();
        IngestScopeKeys.Parse("   ").ShouldBeNull();
    }

    [Fact]
    public void ParseScope_EmptyArray_IsPresentAndEmpty() => IngestScopeKeys.Parse("[]").ShouldBeEmpty();

    [Fact]
    public void ParseScope_MalformedJson_IsAbsent() => IngestScopeKeys.Parse("[nope").ShouldBeNull();

    [Fact]
    public void Resolve_Concurrency_GlobalApplies_WhenNoProjectEntry()
    {
        var config = Resolve(new Dictionary<string, string?> { [WatchConfigKeys.ConcurrencyGlobal] = "8" });
        config.Concurrency.ShouldBe(8);
    }

    [Fact]
    public void Resolve_Concurrency_ProjectBeatsGlobal()
    {
        var settings = new Dictionary<string, string?>
        {
            [WatchConfigKeys.ConcurrencyGlobal] = "8",
            [WatchConfigKeys.ConcurrencyProject("acme")] = "6"
        };
        Resolve(settings).Concurrency.ShouldBe(6);
    }

    [Fact]
    public void Resolve_Concurrency_UnparseableValue_FallsBackToDefault()
    {
        var config = Resolve(new Dictionary<string, string?> { [WatchConfigKeys.ConcurrencyGlobal] = "many" });
        config.Concurrency.ShouldBe(4);
    }
}
