using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Shared state for the file-watcher feature scenarios — one instance per scenario.
///     Real temp dirs under DataRoot, real SqliteMemoryStore, FakeTimeProvider-driven ticks and
///     bounded polling (≤5s real time) for OS event delivery (R7). The watch stack is composed
///     exactly like DI (Dependencies.RegisterMemoryServices): MemoryExtensionHost-decorated
///     IMemoryStore, WatchPipeline/EventSource/CatchUp/HostedService, WatchTools over the guard.
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

    public MemoryExtensionHost Host { get; private set; } = null!;

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
    ///     delivery until the condition holds or the budgets expire. maxFakeSeconds enforces
    ///     feature timing claims ("within one second"); maxRealSeconds bounds OS delivery (R7).
    /// </summary>
    public async Task<bool> StepUntilAsync(Func<Task<bool>> condition, int maxFakeSeconds = 2,
        int maxRealSeconds = 5, CancellationToken cancellationToken = default)
    {
        var realDeadline = DateTime.UtcNow.AddSeconds(maxRealSeconds);
        var fakeStart = TimeProvider.GetUtcNow();
        while (!await condition().ConfigureAwait(false))
        {
            if (TimeProvider.GetUtcNow() - fakeStart >= TimeSpan.FromSeconds(maxFakeSeconds) ||
                DateTime.UtcNow >= realDeadline)
            {
                return false;
            }

            TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            await Pipeline.TickOnceAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string projectId, string query,
        CancellationToken cancellationToken = default) =>
        Host.SearchAsync(new SearchQuery(projectId, query), cancellationToken);

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
        Store.SetSettingAsync(WatchConfigKeys.ScopeProject(projectId), WatchConfigKeys.SerializeScope(paths),
            cancellationToken);

    public Task SetWatchScopeGlobalAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(WatchConfigKeys.ScopeGlobal, WatchConfigKeys.SerializeScope(paths), cancellationToken);

    /// <summary>Adds one path to the global scope, keeping what is already there.</summary>
    public async Task AddWatchScopeGlobalAsync(string path, CancellationToken cancellationToken = default)
    {
        var existing = WatchConfigKeys.ParseScope(
            await Store.GetSettingAsync(WatchConfigKeys.ScopeGlobal, cancellationToken)) ?? [];
        await SetWatchScopeGlobalAsync(existing.Append(path).Distinct(WatchPath.PathComparer), cancellationToken);
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
                     WatchConfigKeys.ScopeProject(projectId), WatchConfigKeys.ScopeGlobal,
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
        Host = new MemoryExtensionHost(Store, []);
        var scanGuard = new WatchScanGuard();
        Pipeline = new WatchPipeline(new WatchScheduler(),
            new WatchDigestExecutor(Host, WatchStore, Host, TimeProvider, NullLogger<WatchDigestExecutor>.Instance), new WatchRetryPolicy(), Host,
            TimeProvider, scanGuard, NullLogger<WatchPipeline>.Instance);
        EventSource = new WatchEventSource(Pipeline.Enqueue, Errors.Add, NullLogger<WatchEventSource>.Instance);
        CatchUp = new WatchCatchUp(Pipeline, WatchStore, scanGuard,
            new SqliteWatchScanLease(Factory, TimeProvider), TimeProvider, NullLogger<WatchCatchUp>.Instance);
        Hosted = new WatchHostedService(Host, WatchStore, Pipeline, EventSource, CatchUp, TimeProvider,
            NullLogger<WatchHostedService>.Instance);
        Service = new WatchService(WatchStore, Host, Pipeline, TimeProvider);
        Metrics?.Dispose();
        var metrics = new ToolCallMetrics();
        Metrics = metrics;
        Tools = new WatchTools(Service, new ToolGate(new MemoryAccessGuard(Host), new FakePromotionQueue()), metrics);
    }
}
