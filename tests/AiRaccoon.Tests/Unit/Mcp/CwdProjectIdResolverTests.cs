using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Projects;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The cwd-default projectId resolver: scopes (ingest.scope.* settings rows) and live watch
///     registrations are one union of candidates — a project qualifies when either surface's path
///     contains the working directory. One distinct project resolves; several are refused as
///     ambiguous (never guess); none yields None. Stored ids travel verbatim — canonicalization is
///     the gate's single job, not the resolver's.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CwdProjectIdResolverTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("cwd-resolver");
    private readonly InMemorySettings _settings = new();

    public void Dispose() => TestData.DeleteTempRoot(_root);

    private string Dir(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private CwdProjectIdResolver NewResolver(string cwd) => new(_settings, _settings, new PinnedCwdProbe(cwd));

    private sealed class PinnedCwdProbe(string cwd) : ICwdProbe
    {
        public string CurrentDirectory => cwd;
    }

    [Fact]
    public async Task ExactMatch_ResolvesProject()
    {
        var cwd = Dir("repo-a");
        _settings.Values[IngestScopeKeys.ScopeProject("p1")] = IngestScopeKeys.Serialize([cwd]);

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        resolution.ShouldBe(new ProjectIdResolution.Resolved("p1"));
    }

    [Fact]
    public async Task AncestorScope_ContainsCwdSubdirectory()
    {
        // The scope entry is an ancestor of the cwd: entry covers all subdirectories.
        var scope = Dir("repo");
        var cwd = Path.Combine(scope, "src");
        Directory.CreateDirectory(cwd);
        _settings.Values[IngestScopeKeys.ScopeProject("p1")] = IngestScopeKeys.Serialize([scope]);

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        resolution.ShouldBe(new ProjectIdResolution.Resolved("p1"));
    }

    [Fact]
    public async Task TwoProjects_BothContain_Ambiguous_Sorted()
    {
        // Seeded p2-first so the refusal order can only come from sorting, not insertion.
        var cwd = Dir("shared");
        _settings.Values[IngestScopeKeys.ScopeProject("p2")] = IngestScopeKeys.Serialize([cwd]);
        _settings.Values[IngestScopeKeys.ScopeProject("p1")] = IngestScopeKeys.Serialize([cwd]);

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        // Element-wise: record equality on IReadOnlyList is reference-fragile across collection types.
        var ambiguous = resolution.ShouldBeOfType<ProjectIdResolution.Ambiguous>();
        ambiguous.SortedIds.ShouldBe(["p1", "p2"]);
    }

    [Fact]
    public async Task NoSurfaceContains_ReturnsNone()
    {
        _settings.Values[IngestScopeKeys.ScopeProject("p1")] = IngestScopeKeys.Serialize([Dir("unrelated")]);

        var resolution = await NewResolver(Dir("elsewhere")).ResolveAsync(TestContext.Current.CancellationToken);

        resolution.ShouldBe(new ProjectIdResolution.None());
    }

    [Fact]
    public async Task WatchOnly_ProjectResolves()
    {
        // No scope rows at all: the watch registration surface alone resolves.
        var watchDir = Dir("watched");
        var cwd = Path.Combine(watchDir, "nested");
        Directory.CreateDirectory(cwd);
        _settings.Watches = [new WatchRegistration("w1", watchDir, 0, 0)];

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        resolution.ShouldBe(new ProjectIdResolution.Resolved("w1"));
    }

    [Fact]
    public async Task GlobalScopeKey_Skipped()
    {
        // ingest.scope.global names every machine's allowlist — it must never elect a project.
        _settings.Values[IngestScopeKeys.ScopeGlobal] = IngestScopeKeys.Serialize([Dir("global-scope")]);
        var cwd = Path.Combine(_root, "global-scope");
        Directory.CreateDirectory(cwd);

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        resolution.ShouldBe(new ProjectIdResolution.None());
    }

    [Fact]
    public async Task ResolverReturnsStoredIdVerbatim_NoCanonicalization()
    {
        var cwd = Dir("legacy");
        _settings.Values[IngestScopeKeys.ScopeProject("ABC-Not-Guid")] = IngestScopeKeys.Serialize([cwd]);

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        // A non-guid id passes through untouched; the gate canonicalizes exactly once downstream.
        resolution.ShouldBe(new ProjectIdResolution.Resolved("ABC-Not-Guid"));
    }

    [Fact]
    public async Task MalformedScopeValue_SkippedNotFatal()
    {
        var cwd = Dir("broken-scope");
        _settings.Values[IngestScopeKeys.ScopeProject("p1")] = "{not json";

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        resolution.ShouldBe(new ProjectIdResolution.None());
    }

    [Fact]
    public async Task ScopeAndWatch_DifferentProjects_Ambiguous()
    {
        // Pins the union rule: scope-beats-watch filtering would resolve p1 here instead of refusing.
        var cwd = Dir("both-surfaces");
        _settings.Values[IngestScopeKeys.ScopeProject("p1")] = IngestScopeKeys.Serialize([cwd]);
        _settings.Watches = [new WatchRegistration("p2", _root, 0, 0)];

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        // Element-wise: record equality on IReadOnlyList is reference-fragile across collection types.
        var ambiguous = resolution.ShouldBeOfType<ProjectIdResolution.Ambiguous>();
        ambiguous.SortedIds.ShouldBe(["p1", "p2"]);
    }

    [Fact]
    public async Task ScopeAndWatch_SameProject_DedupToResolved()
    {
        var cwd = Dir("same-project");
        _settings.Values[IngestScopeKeys.ScopeProject("p1")] = IngestScopeKeys.Serialize([cwd]);
        _settings.Watches = [new WatchRegistration("p1", _root, 0, 0)];

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        resolution.ShouldBe(new ProjectIdResolution.Resolved("p1"));
    }

    /// <summary>
    ///     P4 resolver coherence: an alias pair across the two surfaces (loser scope row, winner
    ///     watch row) stays Ambiguous — the resolver never folds or guesses, even for a known
    ///     fold. Fail-closed by design; the repair removes the pair.
    ///     Ledger — dedup-on-alias : --filter AliasPair_StaysAmbiguous_NeverFolds : loser scope +
    ///     winner watch over one cwd.
    /// </summary>
    [Fact]
    public async Task AliasPair_StaysAmbiguous_NeverFolds()
    {
        var cwd = Dir("renaming");
        // Raw loser key on purpose: the factory folds at construction now, so only a pre-repair
        // bank still holds this spelling — exactly the mid-rename window this pins.
        _settings.Values["ingest.scope.job-search-ai-assistant"] =
            IngestScopeKeys.Serialize([cwd]);
        _settings.Watches = [new WatchRegistration("jsaa", cwd, 0, 0)];

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        var ambiguous = resolution.ShouldBeOfType<ProjectIdResolution.Ambiguous>();
        ambiguous.SortedIds.ShouldBe(["job-search-ai-assistant", "jsaa"]);
    }

    [Fact]
    public async Task MixedSpellings_SameGuid_DedupToResolved()
    {
        // The same guid under two spellings — braced-upper in a scope row, lowercase-D in a watch
        // row — is ONE project: dedup runs on the canonical form, not the stored spelling. The
        // first-seen spelling (the scope row's) is what the resolution carries; the gate
        // canonicalizes it to the D-form exactly once.
        var guid = Guid.NewGuid();
        var bracedUpper = $"{{{guid.ToString("D").ToUpperInvariant()}}}";
        var cwd = Dir("mixed-spellings");
        // Raw key on purpose: the factory folds at construction now (P4), so seeding through
        // it would store the D-form twice and stop exercising the resolver's cross-spelling
        // dedup — exactly the pre-repair bank shape this pins (cf. AliasPair_StaysAmbiguous_NeverFolds).
        _settings.Values[$"ingest.scope.{bracedUpper}"] = IngestScopeKeys.Serialize([cwd]);
        _settings.Watches = [new WatchRegistration(guid.ToString("D"), cwd, 0, 0)];

        var resolution = await NewResolver(cwd).ResolveAsync(TestContext.Current.CancellationToken);

        var resolved = resolution.ShouldBeOfType<ProjectIdResolution.Resolved>();
        resolved.ProjectId.ShouldBe(bracedUpper);
        ProjectId.Canonicalize(resolved.ProjectId).ShouldBe(guid.ToString("D"));
    }
}
