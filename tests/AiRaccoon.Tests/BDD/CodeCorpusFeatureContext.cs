using AiRaccoon.Access;
using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Tests;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Shared state for docs/features/code-corpus/code-corpus.feature — one instance per scenario.
///     Deliberately does NOT reuse <see cref="MemoryFeatureContext" />/<see cref="FileWatcherFeatureContext" />:
///     those build their Store's FileIngestor without an <see cref="IIgnoreRulesProvider" /> or
///     <see cref="IWatchStore" /> wired in (TestData.CreateMemoryStore does not expose those
///     parameters), which several code-corpus scenarios need for direct memory_ingest_file/
///     memory_ingest_directory calls to honor ai-raccoon.ignore. Composes the same real stack
///     (SqliteMemoryStore, the watch pipeline, MemoryTools/CodeTools/WatchTools) by hand instead,
///     with a StubCodeChunker (real line ranges, no token-budget/brace logic — WP2 concern, not
///     this feature's) and a real CodeEmbedder over a FakeCodeEmbeddingService (deterministic
///     768-dim vectors, no HF download, no real model I/O — mirrors CodeReindexJobTests).
/// </summary>
public sealed class CodeCorpusFeatureContext : IDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly IModelMigrationLease ModelMigrationLease = Substitute.For<IModelMigrationLease>();

    public IEventPump<EmbedDrainRequest> EmbedDrainPump { get; } = TestData.NewEmbedDrainPump();

    public CodeCorpusFeatureContext()
    {
        DataRoot = TestData.CreateTempRoot("airaccoon-code-corpus-bdd");
        RepoDir = Path.Combine(DataRoot, "repo");
        Directory.CreateDirectory(RepoDir);

        var options = new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        Factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        TimeProvider = new FakeTimeProvider(FixedNow);
        Settings = new SqliteSettingsStore(Factory);
        FakeEmbeddingService = new FakeCodeEmbeddingService();
        CodeEmbedder = new CodeEmbedder(FakeEmbeddingService, NullLogger<CodeEmbedder>.Instance, new VecDimensionReconciler());

        WatchStore = new WatchStore(Factory);
        Store = ComposeStore();
        CodeSearch = new SqliteCodeSearchService(Factory, CodeEmbedder);
        CodeEngineStore = new SqliteCodeEngineStore(Factory, FakeEmbeddingService,
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
            TestData.CreateManifestPoolingRepair(), new VecDimensionReconciler());
        SearchQuality = new SqliteSearchQualityService(Factory, NullLogger<SqliteSearchQualityService>.Instance);
        ReindexJob = new CodeReindexJob(CodeEmbedder, EmbedDrainPump);

        var gate = new ToolGate(new MemoryAccessGuard(Store), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), new NeverMigratedGate());
        MemoryTools = new MemoryTools(Store, gate,
            new SearchDispatcher(Store, CodeSearch, SearchQuality),
            new QueryGuardService(Settings), new MemoryWriteService(Store, new FakePromotionQueue()),
            new NoOpMeasurementRecorder(), NullLogger<MemoryTools>.Instance);
        CodeTools = new CodeTools(CodeSearch, gate);

        ComposeWatchStack();
    }

    /// <summary>Canonical watched directory — the feature's "/repo" maps here.</summary>
    public string RepoDir { get; }

    public string DataRoot { get; }

    public SqliteConnectionFactory Factory { get; }

    public FakeTimeProvider TimeProvider { get; }

    public SqliteSettingsStore Settings { get; }

    public SqliteMemoryStore Store { get; }

    public IWatchStore WatchStore { get; }

    public WatchPipeline Pipeline { get; private set; } = null!;

    public WatchEventSource EventSource { get; private set; } = null!;

    public WatchCatchUp CatchUp { get; private set; } = null!;

    public WatchHostedService Hosted { get; private set; } = null!;

    public IWatchService WatchServiceInstance { get; private set; } = null!;

    public WatchTools WatchToolsInstance { get; private set; } = null!;

    public ICodeSearchService CodeSearch { get; }

    public ICodeEngineStore CodeEngineStore { get; }

    public ISearchQualityService SearchQuality { get; }

    public CodeReindexJob ReindexJob { get; }

    public MemoryTools MemoryTools { get; }

    public CodeTools CodeTools { get; }

    /// <summary>Backs <see cref="CodeEmbedder" />: deterministic vectors, scriptable failure for the
    /// "configured but unloadable" scenario, no real model I/O ever.</summary>
    public FakeCodeEmbeddingService FakeEmbeddingService { get; }

    public ICodeEmbedder CodeEmbedder { get; }

    public void Dispose()
    {
        EventSource.StopAll();
        TestData.DeleteTempRoot(DataRoot);
    }

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

    /// <summary>Writes a file (dirs created) and stamps its mtime at the current fake time.</summary>
    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, TimeProvider.GetUtcNow().UtcDateTime);
    }

    public Task SetWatchEnabledGlobalAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(WatchConfigKeys.EnabledGlobal, enabled ? "true" : "false", cancellationToken);

    public Task SetWatchScopeGlobalAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        Store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize(paths), cancellationToken);

    /// <summary>One reconcile pass + await the catch-up scan it enqueued (mirrors FileWatcherFeatureContext).</summary>
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        await Hosted.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        if (CatchUp.LastScan is { } scan)
        {
            await scan.ConfigureAwait(false);
        }
    }

    /// <summary>Bounded poll: reconcile (registrations + catch-up scans) + drain the pipeline's
    /// event queue + a short real sleep for OS event delivery, until the condition holds or the
    /// budget expires (generous hang-stop only, not a timing assertion).</summary>
    public async Task<bool> StepUntilAsync(Func<Task<bool>> condition, int maxAttempts = 30,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await ReconcileOnceAsync(cancellationToken).ConfigureAwait(false);
            await Pipeline.TickOnceAsync(cancellationToken).ConfigureAwait(false);
            if (await condition().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return await condition().ConfigureAwait(false);
    }

    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) =>
        await Factory.OpenBankAsync(cancellationToken);

    /// <summary>Registers a watch through the real service, bypassing the agent access guard (background setup).</summary>
    public async Task SetupWatchAsync(string projectId, string virtualPath, CancellationToken cancellationToken = default)
    {
        var path = MapPath(virtualPath);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        await SetWatchEnabledGlobalAsync(true, cancellationToken);
        await AddWatchScopeGlobalAsync(path, cancellationToken);
        await WatchServiceInstance.AddAsync(projectId, path, cancellationToken);
        await ReconcileOnceAsync(cancellationToken);
    }

    /// <summary>Adds one path to the global ingest-scope allowlist, keeping what is already there —
    /// needed even without a watch: FileIngestor's ignore-root resolution falls back to a scope
    /// allowlist entry when no watch covers the target (docs/work/2026-08-21-code-search-implementation-plan.md §2.1).</summary>
    public async Task AddWatchScopeGlobalAsync(string path, CancellationToken cancellationToken = default)
    {
        var existing = IngestScopeKeys.Parse(
            await Store.GetSettingAsync(IngestScopeKeys.ScopeGlobal, cancellationToken)) ?? [];
        await SetWatchScopeGlobalAsync(existing.Append(path).Distinct(IngestPath.PathComparer), cancellationToken);
    }

    private SqliteMemoryStore ComposeStore()
    {
        var sourceStore = new SqliteMemorySourceStore(Factory);
        var markdownChunker = TestData.RealMarkdownChunker();
        var matcher = new FileTypeMatcher(
        [
            new MarkdownFileTypeHandler(markdownChunker),
            new JsonFileTypeHandler(TestData.RealJsonChunker(markdownChunker))
        ]);
        var embeddings = TestData.CreateEmbeddingService();
        var embedder = TestData.CreateEntryEmbedder(embeddings, ModelMigrationLease, TimeProvider, new VecDimensionReconciler());
        var codeFileTypeMatcher = new CodeFileTypeMatcher();
        var codeIngestor = new CodeIngestor(codeFileTypeMatcher, new StubCodeChunker(), TimeProvider);
        var fileIngestor = new FileIngestor(matcher, sourceStore, TimeProvider, embeddings,
            new IgnoreRulesProvider(), codeFileTypeMatcher, codeIngestor, WatchStore, EmbedDrainPump);
        var noiseFilteringService = new NoiseFilteringService([]);
        return new SqliteMemoryStore(Factory, sourceStore, fileIngestor, embedder, TimeProvider,
            NullLogger<SqliteMemoryStore>.Instance, noiseFilteringService, Settings, EmbedDrainPump,
            NoOpMeasurementRecorder.Instance);
    }

    private void ComposeWatchStack()
    {
        var scanGuard = new WatchScanGuard();
        WatchCatchUp? catchUp = null;
        Pipeline = new WatchPipeline(new WatchScheduler(),
            new WatchDigestExecutor(Store, WatchStore, TimeProvider,
                new IgnoreRulesProvider(), new Lazy<IWatchScanInitiator>(() => catchUp!), EmbedDrainPump,
                new SqliteProjectIdsMigrationGate(Factory)),
            new WatchRetryPolicy(), scanGuard, Store, TimeProvider,
            NullLogger<WatchPipeline>.Instance);
        EventSource = new WatchEventSource(Pipeline.Enqueue, _ => { }, NullLogger<WatchEventSource>.Instance);
        CatchUp = catchUp = new WatchCatchUp(Pipeline, WatchStore, scanGuard,
            new SqliteWatchScanLease(Factory, TimeProvider), TimeProvider, NullLogger<WatchCatchUp>.Instance,
            new IgnoreRulesProvider());
        Hosted = new WatchHostedService(Store, WatchStore, Pipeline, EventSource, CatchUp, TimeProvider,
            TestTelemetry.None, NullLogger<WatchHostedService>.Instance);
        WatchServiceInstance = new WatchService(WatchStore, Store, Pipeline, TimeProvider, new WatchOverlapResolver(),
            new SqliteProjectIdsMigrationGate(Factory));
        WatchToolsInstance = new WatchTools(WatchServiceInstance, new ToolGate(new MemoryAccessGuard(Store), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), new NeverMigratedGate()));
    }
}
