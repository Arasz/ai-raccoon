using AiRaccoon.Core.Ingestion;
using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Shared state for the file-watcher feature scenarios — one instance per scenario. Real
///     temp dirs, a real SqliteMemoryStore and FakeTimeProvider-driven ticks; the watch stack is
///     composed exactly like DI (Dependencies.RegisterMemoryServices).
/// </summary>
public sealed class FileWatcherFeatureContext : MemoryFeatureContext
{
    public FileWatcherFeatureContext()
    {
        RepoDir = Path.Combine(DataRoot, "repo");
        Directory.CreateDirectory(RepoDir);
        ComposeStack();
    }

    /// <summary>Canonical watched directory — the feature's "/repo" maps here.</summary>
    public string RepoDir { get; }

    public WatchStore WatchStore { get; private set; } = null!;

    public WatchPipeline Pipeline { get; private set; } = null!;

    public WatchEventSource EventSource { get; private set; } = null!;

    public WatchCatchUp CatchUp { get; private set; } = null!;

    public WatchHostedService Hosted { get; private set; } = null!;

    public WatchService Service { get; private set; } = null!;

    public WatchTools Tools { get; private set; } = null!;

    /// <summary>Adapter-level error events collected by the event source callback (DI wires these to the logger).</summary>
    public List<WatchEventError> Errors { get; } = [];

    private ToolCallMetrics? Metrics { get; set; }

    /// <summary>Maps a feature-file path ("/repo", "/repo/docs", "/other") to a real path under this scenario's DataRoot.</summary>
    public string MapPath(string virtualPath)
    {
        if (virtualPath == "/repo")
        {
            return RepoDir;
        }

        if (virtualPath.StartsWith("/repo/", StringComparison.Ordinal))
        {
            return Path.Combine(RepoDir, virtualPath["/repo/".Length..]);
        }

        return Path.Combine(DataRoot, virtualPath.TrimStart('/'));
    }

    /// <summary>Stops the watch machinery (watchers + metrics); the bank dir is deleted by the base Dispose.</summary>
    public void StopWatchStack()
    {
        EventSource.StopAll();
        Metrics?.Dispose();
    }

    /// <summary>
    ///     "Server restart": watchers down, fresh stack over the same bank. Settings and watch
    ///     registrations persist in memory.db; the next reconcile re-watches + catch-up scans.
    /// </summary>
    public void Restart()
    {
        EventSource.StopAll();
        ComposeStack();
    }

    /// <summary>
    ///     One reconcile pass + await the catch-up scan it enqueued. Idempotent — the hosted
    ///     loop reconciles every second in production, so extra passes are harmless; new
    ///     registrations get a watcher + initial scan exactly once.
    /// </summary>
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        await Hosted.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        if (CatchUp.LastScan is { } scan)
        {
            await scan.ConfigureAwait(false);
        }
    }

    /// <summary>One deterministic pipeline tick: advance the fake clock, drain the event channel.</summary>
    public async Task RunTickAsync(CancellationToken cancellationToken = default)
    {
        TimeProvider.Advance(WatchPipeline.TickInterval);
        await Pipeline.TickOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Bounded poll: advance 100ms fake time + one tick + a short real sleep for OS event
    ///     delivery until the condition holds or the budgets expire. maxRealSeconds is a generous
    ///     hang-stop only, not the timing assertion (docs/work/archive/2026-08-06-http-serve-code-review-gate.md).
    /// </summary>
    public async Task<bool> StepUntilAsync(Func<Task<bool>> condition, int maxFakeSeconds = 2,
        int maxRealSeconds = 30, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var realDeadline = startedAt.AddSeconds(maxRealSeconds);
        var fakeStart = TimeProvider.GetUtcNow();
        var steps = 0;
        while (!await condition().ConfigureAwait(false))
        {
            var fakeSpent = TimeProvider.GetUtcNow() - fakeStart;
            var fakeExpired = fakeSpent >= TimeSpan.FromSeconds(maxFakeSeconds);
            var realExpired = DateTime.UtcNow >= realDeadline;
            if (fakeExpired || realExpired)
            {
                // Which budget ran out is the whole diagnosis, and the caller only sees a bool.
                // Without this a load-induced timeout and a genuine behaviour break read alike.
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"StepUntilAsync gave up after {steps} steps: " +
                    $"{(fakeExpired ? "fake-time budget" : "real-time hang-stop")} expired " +
                    $"(fake {fakeSpent.TotalSeconds:F1}s/{maxFakeSeconds}s, " +
                    $"real {(DateTime.UtcNow - startedAt).TotalSeconds:F1}s/{maxRealSeconds}s)");
                return false;
            }

            steps++;
            TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            await Pipeline.TickOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string projectId, string query,
        CancellationToken cancellationToken = default) =>
        Store.SearchAsync(new SearchQuery(projectId, query), cancellationToken);

    /// <summary>Writes a file (dirs created) and stamps its mtime at the current fake time.</summary>
    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, TimeProvider.GetUtcNow().UtcDateTime);
    }

    public Task SetWatchEnabledAsync(string projectId, bool enabled, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(WatchConfigKeys.EnabledProject(projectId), enabled ? "true" : "false", cancellationToken);

    public Task SetWatchEnabledGlobalAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(WatchConfigKeys.EnabledGlobal, enabled ? "true" : "false", cancellationToken);

    public Task SetWatchScopeAsync(string projectId, IEnumerable<string> paths,
        CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(IngestScopeKeys.ScopeProject(projectId), IngestScopeKeys.Serialize(paths),
            cancellationToken);

    public Task SetWatchScopeGlobalAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize(paths), cancellationToken);

    /// <summary>Adds one path to the global scope, keeping what is already there.</summary>
    public async Task AddWatchScopeGlobalAsync(string path, CancellationToken cancellationToken = default)
    {
        var existing = IngestScopeKeys.Parse(
            await Store.GetSettingAsync(IngestScopeKeys.ScopeGlobal, cancellationToken)) ?? [];
        await SetWatchScopeGlobalAsync(existing.Append(path).Distinct(IngestPath.PathComparer), cancellationToken);
    }

    public Task SetConcurrencyGlobalAsync(int value, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(WatchConfigKeys.ConcurrencyGlobal, value.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public Task SetAccessTierAsync(string projectId, string tier, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), tier, cancellationToken);

    /// <summary>Resolved watch config for a project (project entry wins over global, exactly like WatchService).</summary>
    public async Task<WatchConfig> ResolveConfigAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in new[]
                 {
                     WatchConfigKeys.EnabledProject(projectId), WatchConfigKeys.EnabledGlobal,
                     IngestScopeKeys.ScopeProject(projectId), IngestScopeKeys.ScopeGlobal,
                     WatchConfigKeys.ConcurrencyProject(projectId), WatchConfigKeys.ConcurrencyGlobal
                 })
        {
            values[key] = await Store.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return WatchConfig.Resolve(projectId, key => values.GetValueOrDefault(key));
    }

    private void ComposeStack()
    {
        WatchStore = new WatchStore(Factory);
        var scanGuard = new WatchScanGuard();
        Pipeline = new WatchPipeline(new WatchScheduler(),
            new WatchDigestExecutor(Store, WatchStore, TimeProvider, NullLogger<WatchDigestExecutor>.Instance), new WatchRetryPolicy(), Store,
            TimeProvider, scanGuard, NullLogger<WatchPipeline>.Instance);
        EventSource = new WatchEventSource(Pipeline.Enqueue, Errors.Add, NullLogger<WatchEventSource>.Instance);
        CatchUp = new WatchCatchUp(Pipeline, WatchStore, scanGuard,
            new SqliteWatchScanLease(Factory, TimeProvider), TimeProvider, NullLogger<WatchCatchUp>.Instance);
        Hosted = new WatchHostedService(Store, WatchStore, Pipeline, EventSource, CatchUp, TimeProvider,
            NullLogger<WatchHostedService>.Instance);
        Service = new WatchService(WatchStore, Store, Pipeline, TimeProvider);
        Metrics?.Dispose();
        var metrics = new ToolCallMetrics();
        Metrics = metrics;
        Tools = new WatchTools(Service, new ToolGate(new MemoryAccessGuard(Store), new FakePromotionQueue()), metrics);
    }
}
