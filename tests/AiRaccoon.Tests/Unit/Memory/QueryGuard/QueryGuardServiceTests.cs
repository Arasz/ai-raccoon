using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard;

/// <summary>
///     WP2 (docs/plans/2026-08-16-bank-open-cost-implementation.md): <see cref="QueryGuardService" />
///     must make exactly one <see cref="ISettingsStore" /> call per <c>EvaluateAsync</c>, down from
///     2 (typical) or 4 (structural-enabled, non-clean). Defines its own counting double rather than
///     extending a shared fake, per the plan's lane rule (no caller outside this file needs it).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class QueryGuardServiceTests
{
    // A real stack-trace paste (search_quality-shaped): trips QueryGuardPolicy's Warn regex tier
    // directly, so the structural detector never runs.
    private const string WarnQuery =
        """
        info: System.Net.Http.HttpClient.ServerProbe.LogicalHandler[100]
              Start processing HTTP request POST http://127.0.0.1:7721/mcp
        """;

    // A real Hermes background-process notification: trips the Refuse regex tier directly.
    private const string RefuseQuery =
        """
        [IMPORTANT: Background process proc_97aa3ea5eb50 completed normally (exit code 0).
        Command: dotnet test --no-build
        Output:
        ]
        """;

    private const string CleanQuery = "why did the auth build start failing";

    // model-score-fixture.json id 1a747f2e00f313, score 0.9995419257364978 — well above the
    // shipped default threshold (0.98939822280316, docs/adr/0041). Matches neither regex tier
    // (no "at " stack frame, no console log-level prefix), so it reaches the structural detector
    // and the detector scores it Warn — the only query shape that exercises all four settings
    // reads on the pre-WP2 code path.
    private const string StructurallyDetectedQuery = "/api/offers -> 403";

    [Fact]
    public async Task EvaluateAsync_WhenDisabled_ReadsSettingsStoreOnce()
    {
        // Characterisation, not red-then-green: the disabled branch already made exactly one call
        // before WP2 (only queryGuard.enabled.global is read), so this cannot fail on the old code.
        // Kept because acceptance criterion 1 names all five branches explicitly.
        var store = new CountingSettingsStore();
        store.Set(QueryGuardConfigKeys.EnabledGlobal, "false");
        var service = new QueryGuardService(store);

        await service.EvaluateAsync("project", RefuseQuery, TestContext.Current.CancellationToken);

        store.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_ForACleanQuery_ReadsSettingsStoreOnce()
    {
        var store = new CountingSettingsStore();
        var service = new QueryGuardService(store);

        await service.EvaluateAsync("project", CleanQuery, TestContext.Current.CancellationToken);

        store.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_ForAWarnQuery_ReadsSettingsStoreOnce()
    {
        var store = new CountingSettingsStore();
        var service = new QueryGuardService(store);

        await service.EvaluateAsync("project", WarnQuery, TestContext.Current.CancellationToken);

        store.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_ForARefuseQuery_ReadsSettingsStoreOnce()
    {
        var store = new CountingSettingsStore();
        var service = new QueryGuardService(store);

        await service.EvaluateAsync("project", RefuseQuery, TestContext.Current.CancellationToken);

        store.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_ForAStructurallyDetectedQuery_ReadsSettingsStoreOnce()
    {
        var store = new CountingSettingsStore();
        store.Set(QueryGuardConfigKeys.StructuralEnabledGlobal, "true");
        var service = new QueryGuardService(store);

        await service.EvaluateAsync("project", StructurallyDetectedQuery, TestContext.Current.CancellationToken);

        store.CallCount.ShouldBe(1);
    }

    /// <summary>
    ///     Green before and after WP2 by design — batching the reads must not change what the guard
    ///     decides. Not a red-then-green test; it is the regression net for the call-count change.
    /// </summary>
    [Theory]
    [MemberData(nameof(VerdictCases))]
    public async Task EvaluateAsync_VerdictsUnchanged(string query, bool structuralEnabled,
        bool shadow, QueryGuardTier expectedTier, QueryGuardTier? expectedShadowedTier)
    {
        var store = new CountingSettingsStore();
        store.Set(QueryGuardConfigKeys.StructuralEnabledGlobal, structuralEnabled ? "true" : "false");
        store.Set(QueryGuardConfigKeys.ShadowGlobal, shadow ? "true" : "false");
        var service = new QueryGuardService(store);

        var outcome = await service.EvaluateAsync("project", query, TestContext.Current.CancellationToken);

        outcome.Verdict.Tier.ShouldBe(expectedTier);
        var shadowedTier = outcome.Shadowed?.Tier;
        shadowedTier.ShouldBe(expectedShadowedTier);
    }

    public static TheoryData<string, bool, bool, QueryGuardTier, QueryGuardTier?> VerdictCases =>
        new()
        {
            { CleanQuery, false, false, QueryGuardTier.Clean, null },
            { WarnQuery, false, false, QueryGuardTier.Warn, null },
            { RefuseQuery, false, false, QueryGuardTier.Refuse, null },
            { RefuseQuery, false, true, QueryGuardTier.Clean, QueryGuardTier.Refuse },
            { StructurallyDetectedQuery, true, false, QueryGuardTier.Warn, null },
            { StructurallyDetectedQuery, false, false, QueryGuardTier.Clean, null }
        };

    [Fact]
    public async Task EvaluateAsync_ObservesASettingsChangeBetweenCalls()
    {
        // The cross-process liveness contract (§2 of the plan): batching must still read fresh
        // per call. Nothing else in this suite proves the absence of caching.
        var store = new CountingSettingsStore();
        store.Set(QueryGuardConfigKeys.EnabledGlobal, "false");
        var service = new QueryGuardService(store);

        var beforeToggle = await service.EvaluateAsync("project", RefuseQuery, TestContext.Current.CancellationToken);
        store.Set(QueryGuardConfigKeys.EnabledGlobal, "true");
        var afterToggle = await service.EvaluateAsync("project", RefuseQuery, TestContext.Current.CancellationToken);

        beforeToggle.Verdict.Tier.ShouldBe(QueryGuardTier.Clean);
        afterToggle.Verdict.Tier.ShouldBe(QueryGuardTier.Refuse);
    }

    private sealed class CountingSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public int CallCount { get; private set; }

        public void Set(string key, string value) => _values[key] = value;

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            IReadOnlyDictionary<string, string> matches = _values
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return Task.FromResult(matches);
        }

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
