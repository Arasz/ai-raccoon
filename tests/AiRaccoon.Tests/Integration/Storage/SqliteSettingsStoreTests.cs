using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP4 (docs/plans/2026-08-16-bank-open-cost-implementation.md §6, WP4): a pure-test package with
///     no production change. Pins <see cref="SqliteSettingsStore" />'s real contract — the ordinary
///     get/set/delete round trip, the <c>LIKE</c> prefix semantics <c>MemorySql.SelectSettingsByPrefix</c>
///     actually has (not the "exact prefix only" semantics the plan assumed — see the class remarks on
///     each prefix test below), and cross-process liveness (§5.5): a write from a genuinely separate OS
///     process must be visible through an already-running singleton store instance, with no restart.
///     Every semantics claim here was verified against a scratch SQLite database first
///     (sqlite-schema-review's core rule), not against the plan.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteSettingsStoreTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-settings-store");
    private readonly SqliteConnectionFactory _factory;

    /// <summary>
    ///     The one store instance every test in this class reads and writes through — standing in for
    ///     the DI singleton (<c>AddRequiredSingleton&lt;ISettingsStore, SqliteSettingsStore&gt;</c>,
    ///     AppRegistrations.cs:222) that lives for the whole server process. A prior draft of this
    ///     package round-tripped through a second, freshly-constructed factory/store pair instead —
    ///     which proves nothing about a cache, because a cache attached to the one long-lived singleton
    ///     would never be in that second pair's object graph. Reusing this one field is what makes the
    ///     liveness tests below actually load-bearing.
    /// </summary>
    private readonly SqliteSettingsStore _store;

    public SqliteSettingsStoreTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteSettingsStore(_factory);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    // ---- Ordinary contract: characterisation tests, green from the start by design ----

    /// <summary>Characterisation. A key nobody ever wrote is "not found", never a thrown exception.</summary>
    [Fact]
    public async Task GetSettingAsync_ForAMissingKey_ReturnsNull()
    {
        var value = await _store.GetSettingAsync("no.such.key", TestContext.Current.CancellationToken);

        value.ShouldBeNull();
    }

    /// <summary>
    ///     Characterisation. Round-trips through both single-key and prefix reads (behaviour radius:
    ///     the write is not only visible to the key it targeted, it is also visible to the prefix scan
    ///     that would batch it under WP2/WP3).
    /// </summary>
    [Fact]
    public async Task SetSettingAsync_ThenGetSettingAsync_RoundTrips()
    {
        await _store.SetSettingAsync("access.mode.global", "rw", TestContext.Current.CancellationToken);

        (await _store.GetSettingAsync("access.mode.global", TestContext.Current.CancellationToken)).ShouldBe("rw");
        var byPrefix = await _store.GetSettingsByPrefixAsync("access.mode.", TestContext.Current.CancellationToken);
        byPrefix.ShouldContainKeyAndValue("access.mode.global", "rw");
    }

    /// <summary>
    ///     Characterisation, and the "no NULL-versus-absent ambiguity" acceptance criterion (§ WP4):
    ///     `value` is `NOT NULL` in the schema and `SetSettingAsync` guards against a null argument, so
    ///     the only way to prove "present but empty" is distinguishable from "absent" is the empty
    ///     string, not degenerate reasoning about NULL.
    /// </summary>
    [Fact]
    public async Task SetSettingAsync_WithAnEmptyString_IsDistinctFromAnAbsentKey()
    {
        await _store.SetSettingAsync("scratch.empty", string.Empty, TestContext.Current.CancellationToken);

        (await _store.GetSettingAsync("scratch.empty", TestContext.Current.CancellationToken)).ShouldBe(string.Empty);
        (await _store.GetSettingAsync("scratch.never-set", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    /// <summary>Characterisation. Deleting removes the key from both the single-key and prefix paths.</summary>
    [Fact]
    public async Task DeleteSettingAsync_ThenGetSettingAsync_ReturnsNullAgain()
    {
        await _store.SetSettingAsync("queryGuard.enabled.global", "false", TestContext.Current.CancellationToken);

        await _store.DeleteSettingAsync("queryGuard.enabled.global", TestContext.Current.CancellationToken);

        (await _store.GetSettingAsync("queryGuard.enabled.global", TestContext.Current.CancellationToken)).ShouldBeNull();
        var byPrefix = await _store.GetSettingsByPrefixAsync("queryGuard.", TestContext.Current.CancellationToken);
        byPrefix.ShouldNotContainKey("queryGuard.enabled.global");
    }

    // ---- Prefix semantics: characterisation of what MemorySql.SelectSettingsByPrefix actually does ----

    /// <summary>
    ///     Characterisation, and acceptance criterion 2 (WP4): no match is an empty dictionary, never a
    ///     thrown exception or a null. Property intersection with "other data exists": a sibling row
    ///     under a different prefix must not leak into the empty result.
    /// </summary>
    [Fact]
    public async Task GetSettingsByPrefixAsync_WithNoMatchingKeys_ReturnsAnEmptyDictionary()
    {
        await _store.SetSettingAsync("access.mode.global", "rw", TestContext.Current.CancellationToken);

        var result = await _store.GetSettingsByPrefixAsync("noSuchPrefix.", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    /// <summary>
    ///     Characterisation of the real boundary the plan's "exact prefix only" claim gets right: a
    ///     sibling key that shares the prefix's leading characters but diverges at the very next
    ///     character (no literal <c>.</c>) is excluded — verified on a scratch DB first. Property
    ///     intersection: two matching keys plus one non-matching sibling plus one unrelated key, all
    ///     seeded together, so a false "starts with the raw string" implementation would over-match and
    ///     an off-by-one in the SQL would under-match.
    /// </summary>
    [Fact]
    public async Task GetSettingsByPrefixAsync_ExcludesASiblingKeyThatSharesOnlyTheLeadingCharacters()
    {
        await _store.SetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, "true", TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(QueryGuardConfigKeys.ShadowGlobal, "false", TestContext.Current.CancellationToken);
        await _store.SetSettingAsync("queryGuardX.other", "unrelated", TestContext.Current.CancellationToken);
        await _store.SetSettingAsync("access.mode.global", "rw", TestContext.Current.CancellationToken);

        var result = await _store.GetSettingsByPrefixAsync("queryGuard.", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result.ShouldContainKeyAndValue(QueryGuardConfigKeys.EnabledGlobal, "true");
        result.ShouldContainKeyAndValue(QueryGuardConfigKeys.ShadowGlobal, "false");
        result.ShouldNotContainKey("queryGuardX.other");
        result.ShouldNotContainKey("access.mode.global");
    }

    /// <summary>
    ///     Characterisation of a real defect against the plan's assumption, not a defect this package
    ///     may fix (WP4 is production-untouched). `MemorySql.SelectSettingsByPrefix` is
    ///     <c>key LIKE @prefix || '%'</c>, and SQLite's <c>LIKE</c> is ASCII case-insensitive by
    ///     default — verified on a scratch DB, where a prefix of <c>queryGuard.</c> matched a key
    ///     stored as <c>QUERYGUARD.SHADOW.GLOBAL</c>. So "matches exact prefix only" is false as
    ///     written: it is case-insensitive on the SQL side. (The result dictionary itself uses
    ///     <c>StringComparer.Ordinal</c>, so the two differently-cased keys still land as two distinct
    ///     dictionary entries rather than colliding — that part of the acceptance criterion holds.)
    /// </summary>
    [Fact]
    public async Task GetSettingsByPrefixAsync_MatchesADifferentlyCasedKey_BecauseSqliteLikeIsCaseInsensitiveByDefault()
    {
        await _store.SetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, "true", TestContext.Current.CancellationToken);
        await _store.SetSettingAsync("QUERYGUARD.SHADOW.GLOBAL", "false", TestContext.Current.CancellationToken);

        var result = await _store.GetSettingsByPrefixAsync("queryGuard.", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2, "SQLite LIKE is case-insensitive by default, so both rows match the prefix");
        result.ShouldContainKeyAndValue(QueryGuardConfigKeys.EnabledGlobal, "true");
        result.ShouldContainKeyAndValue("QUERYGUARD.SHADOW.GLOBAL", "false", "Ordinal comparison keeps the two casings as distinct entries rather than overwriting one another");
    }

    /// <summary>
    ///     Characterisation of a second real defect against the plan's assumption: <c>LIKE</c> treats
    ///     an underscore in the pattern as a single-character wildcard, and the prefix is concatenated
    ///     directly into the pattern with no escaping — verified on a scratch DB, where a prefix
    ///     argument containing a literal underscore matched keys with a <c>.</c> or an <c>X</c> in that
    ///     position. No real caller passes a prefix containing <c>_</c> today (every prefix in this
    ///     codebase is dot-separated: <c>queryGuard.</c>, <c>access.mode.</c>), so this is latent, not
    ///     exercised in production — but it is what the SQL actually does, and a future caller with an
    ///     underscore anywhere in a settings key namespace would silently over-match.
    /// </summary>
    [Fact]
    public async Task GetSettingsByPrefixAsync_TreatsALiteralUnderscoreInThePrefixArgumentAsASingleCharacterWildcard()
    {
        await _store.SetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, "true", TestContext.Current.CancellationToken); // "queryGuard.enabled.global" — '.' where the wildcard sits
        await _store.SetSettingAsync("queryGuardX.other", "wildcard-hit", TestContext.Current.CancellationToken); // 'X' where the wildcard sits
        await _store.SetSettingAsync("queryGuard_literal.probe", "literal-hit", TestContext.Current.CancellationToken); // the literal underscore itself

        var result = await _store.GetSettingsByPrefixAsync("queryGuard_", TestContext.Current.CancellationToken);

        result.Count.ShouldBe(3, "'_' in the prefix is a single-character wildcard, so it matches any character in that position, including the literal underscore");
        result.ShouldContainKey(QueryGuardConfigKeys.EnabledGlobal);
        result.ShouldContainKey("queryGuardX.other");
        result.ShouldContainKey("queryGuard_literal.probe");
    }

    // ---- Cross-process liveness (§5.5, WP4-T3): the load-bearing test WP8 is gated on ----

    /// <summary>
    ///     WP4-T3, red-then-green in spirit even though there is no production change here: this test
    ///     cannot fail against today's <see cref="SqliteSettingsStore" /> (it opens a fresh bank
    ///     connection on every call, so it is trivially live), which is exactly why
    ///     <see cref="ANaiveCachingDecorator_WouldMaskTheSameWrite_ProvingThisAssertionActuallyPinsLiveness" />
    ///     exists immediately below: it applies the one plausible mutation (an in-memory cache, the
    ///     shape WP8 proposes) to a decorator around this same store, replays the identical
    ///     read-write-read protocol, and shows it goes stale — proof this assertion is capable of
    ///     catching the regression it exists to catch, not just a check that has only ever passed.
    ///     <para />
    ///     The write is a genuine second OS process: the built <c>ai-raccoon</c> binary run via
    ///     <see cref="RaccoonProcess" />, invoking the real <c>settings queryguard disable</c> CLI verb, which
    ///     opens the same bank file through its own fresh DI graph and its own connection pool — not a
    ///     second <see cref="SqliteConnectionFactory" /> constructed in this test process. The read
    ///     before and the read after both go through <see cref="_store" />, the one instance this whole
    ///     test class holds, standing in for the DI singleton (see its field remarks).
    /// </summary>
    [Fact]
    public async Task GetSettingAsync_ObservesAWriteFromASeparateOSProcess_ThroughTheSameSingletonInstance_WithoutRestarting()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        // Baseline read through the singleton, before the external process exists. Absent, per
        // QueryGuardConfigKeys.DefaultEnabled — the store itself has no notion of the default; it
        // just has never seen the key.
        (await _store.GetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await _store.GetSettingAsync(QueryGuardConfigKeys.ShadowGlobal, TestContext.Current.CancellationToken))
            .ShouldBeNull();

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "settings", "queryguard", "disable"],
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"the writer process must exit cleanly; stderr: {run.Stderr}");

        // Same instance, no restart: the write from the other process is visible immediately.
        (await _store.GetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, TestContext.Current.CancellationToken))
            .ShouldBe("false");
        // Behaviour radius: a key the external process never touched stays untouched — guards
        // against a broad-write bug (e.g. an UPSERT with a wrong WHERE) masquerading as liveness.
        (await _store.GetSettingAsync(QueryGuardConfigKeys.ShadowGlobal, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    /// <summary>
    ///     The mutation-tested proof behind the test above. <see cref="NaivelyCachingSettingsStore" /> is
    ///     a test-only decorator — never production code, never referenced outside this file — that
    ///     caches the first read of each key forever, exactly the shape WP8's plan would need to rule
    ///     out. Run through the identical read-write-read protocol as
    ///     <see cref="GetSettingAsync_ObservesAWriteFromASeparateOSProcess_ThroughTheSameSingletonInstance_WithoutRestarting" />,
    ///     it observes the stale value: the external process's write never reaches it. That is the
    ///     demonstration that the assertion in the test above is a real check, not one that could only
    ///     ever pass.
    /// </summary>
    [Fact]
    public async Task ANaiveCachingDecorator_WouldMaskTheSameWrite_ProvingThisAssertionActuallyPinsLiveness()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        var cached = new NaivelyCachingSettingsStore(_store);

        (await cached.GetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, TestContext.Current.CancellationToken))
            .ShouldBeNull(); // populates the decorator's cache with "absent"

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "settings", "queryguard", "disable"],
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"the writer process must exit cleanly; stderr: {run.Stderr}");

        // The real store already sees the write (proven by the test above); the naive cache does not.
        (await _store.GetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, TestContext.Current.CancellationToken))
            .ShouldBe("false");
        (await cached.GetSettingAsync(QueryGuardConfigKeys.EnabledGlobal, TestContext.Current.CancellationToken))
            .ShouldBeNull("a cache that never invalidates would still report the pre-write value, which is exactly what this test class's liveness assertion must catch");
    }

    /// <summary>
    ///     Test-only double, never wired into production DI: caches <see cref="GetSettingAsync" /> per
    ///     key on first read and never invalidates. Stands in for the shape WP8 proposes, so the
    ///     liveness test above has something concrete to fail against.
    /// </summary>
    private sealed class NaivelyCachingSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

        public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var value = await inner.GetSettingAsync(key, cancellationToken);
            _cache[key] = value;
            return value;
        }

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
            inner.SetSettingAsync(key, value, cancellationToken);

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) => inner.GetSettingsByPrefixAsync(prefix, cancellationToken);

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.DeleteSettingAsync(key, cancellationToken);
    }
}
