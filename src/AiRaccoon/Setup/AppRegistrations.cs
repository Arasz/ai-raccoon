using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.Observability;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using AiRaccoon.Projects;
using AiRaccoon.Setup.Models;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Setup;

public static partial class AppRegistrations
{
    extension(IServiceCollection services)
    {
        public void RegisterCoreMemoryServices(InfrastructureOptions options)
        {
            services.AddSingleton(options);
            services.AddHttpClient();
            services.AddSingleton(TimeProvider.System);
            services.RegisterEncryptionServices(options);
            // WP11: EmbeddingService and SqliteMemoryStore (both wired below) take
            // IMeasurementRecorder — the narrower CLI graph that calls only this method (never
            // RegisterMemoryServices/RegisterMetricsServices) still needs it resolvable, so the
            // buffer + best-effort recorder register here. Only the persistence side (IMetricsStore,
            // MetricsFlusher, IMetricsReportService) stays server-only in RegisterMetricsServices.
            services.RegisterMeasurementRecording();
            // EntryEmbedder (RegisterEmbeddingServices below) takes IOperationTelemetry; the
            // narrower CLI graph that calls only this method still constructs it through
            // SqliteMemoryStore, so the port must be resolvable there too. BackgroundTelemetry is
            // cheap to build and unused by CLI verbs.
            services.AddSingleton<IOperationTelemetry, BackgroundTelemetry>();
            services.RegisterEmbeddingServices();
            services.RegisterFileIngestionServices();
            services.RegisterStores();
        }

        public void RegisterMemoryServices(InfrastructureOptions options, IReadOnlyCollection<McpTransport> mcpTransport)
        {
            services.RegisterEmbedDrainServices();
            services.RegisterPromotionQueue();
            services.RegisterWatchServices();
            services.RegisterSyncServices();
            services.RegisterCoreMemoryServices(options);
            services.RegisterWorkspaceService();
            services.RegisterAccessGuardServices();
            services.RegisterObservabilityServices();
            services.RegisterExtractionServices();
            services.RegisterLongLivedBackgroundServices(mcpTransport);
            services.RegisterBankMaintenanceBackgrounbdService();
            services.RegisterMetricsServices();
        }

        private void RegisterWorkspaceService() => services.AddSingleton<IWorkspaceService, WorkspaceService>();

        private void RegisterExtractionServices()
        {
            services.AddRequiredSingleton<ISharedExtractionService, SharedExtractionService>();
            services.AddRequiredSingleton<ISharedExtractionRunner, SharedExtractionRunner>();
            // docs/adr/0065: share-extract and the read-path query guard are services, not
            // MCP-only, so the CLI and the background loop can reach them too.
            services.AddRequiredSingleton<IShareExtractService, ShareExtractService>();
            services.AddRequiredSingleton<IQueryGuardService, QueryGuardService>();
            // docs/adr/0067: composes the store and the queue here — PromotionQueueService
            // already takes IMemoryStore, so the store itself cannot own this without a cycle.
            services.AddRequiredSingleton<IMemoryWriteService, MemoryWriteService>();
            // memory_search's kind dispatch (which legs run, the code-scope rule, the
            // search_quality exclusion) lives here, off the tool layer (mcp.instructions.md).
            services.AddRequiredSingleton<ISearchDispatcher, SearchDispatcher>();
        }

        private void RegisterSyncServices()
        {
            services.AddRequiredSingleton<ISyncCloudStoreFactory, SyncCloudStoreFactory>();
            services.AddSingleton<ISyncService>(sp => new SyncService(
                ct => sp.GetRequiredService<SyncCloudStoreFactory>().CreateAsync(ct),
                async ct => await sp.GetRequiredService<SqliteConnectionFactory>().OpenBankAsync(ct),
                (path, ct) => OpenSnapshotWithKey(sp, path, ct),
                (path, ct) => OpenSnapshotReadOnly(sp, path, ct),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SyncService>()));
            return;

            static async Task<string> SnapshotConnectionString(IServiceProvider sp, string path, bool readOnly, CancellationToken ct)
            {
                var key = (await sp.GetRequiredService<IEncryptionKeyResolver>().ResolveAsync(ct)).Passphrase;
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
                };
                if (key is not null)
                {
                    csb.Password = key;
                }

                return csb.ToString();
            }

            static async Task<SqliteConnection> OpenSnapshotWithKey(IServiceProvider sp, string path, CancellationToken ct)
            {
                var conn = new SqliteConnection(await SnapshotConnectionString(sp, path, false, ct));
                await conn.OpenAsync(ct);
                conn.EnableExtensions();
                conn.LoadVector();
                return conn;
            }

            static async Task<SqliteConnection> OpenSnapshotReadOnly(IServiceProvider sp, string path, CancellationToken ct)
            {
                var conn = new SqliteConnection(await SnapshotConnectionString(sp, path, true, ct));
                await conn.OpenAsync(ct);
                conn.EnableExtensions();
                conn.LoadVector();
                return conn;
            }
        }

        private void RegisterAccessGuardServices()
        {
            services.AddRequiredSingleton<IMemoryAccessGuard, MemoryAccessGuard>();
            services.AddRequiredSingleton<IForgettingPolicyService, ForgettingPolicyService>();
            // P3's mechanical gate (review M1): the marker ToolGate and the registration guard
            // both consult before any fold-or-refuse. Registered beside its consumers, never null.
            services.AddRequiredSingleton<IProjectIdsMigrationGate, SqliteProjectIdsMigrationGate>();
            services.AddRequiredSingleton<IProjectRegistrationGuard, ProjectRegistrationGuard>();
            services.AddRequiredSingleton<ICwdProbe, CurrentDirectoryCwdProbe>();
            services.AddRequiredSingleton<IProjectIdResolver, CwdProjectIdResolver>();
            services.AddRequiredSingleton<IToolGate, ToolGate>();
        }

        private void RegisterObservabilityServices()
        {
            services.AddRequiredSingleton<ISearchQualityService, SqliteSearchQualityService>();
            services.AddRequiredSingleton<IToolCallMetrics, ToolCallMetrics>();
            // typeof(MemoryTools).Assembly matches VersionContractTests' convention for "the built AiRaccoon binary".
            services.AddSingleton<IBuildStamp>(new AssemblyBuildStamp(typeof(MemoryTools).Assembly));
        }

        private void RegisterBankMaintenanceBackgrounbdService()
        {
            services.AddRequiredSingleton<ISweepService, SweepService>();
            services.AddSingleton<MaintenanceJobRunner>();
            // The list is the schedule (ADR-0070). Order matters: a reclaim after the backfill
            // collects the pages the backfill's deletes freed as well, and PendingEmbedJob is LAST
            // so any job earlier in this list that leaves rows pending — chunk-backfill produced
            // 13,578 of them on a real bank — is already visible to PendingEmbedJob.HasWorkAsync by
            // the time MaintenanceJobRunner's single foreach reaches it, embedding them in this same
            // pass instead of the next one.
            services.AddSingleton<IReadOnlyList<IMaintenanceJob>>(sp =>
            [
                new ChunkBackfillJob(sp.GetRequiredService<IMarkdownChunker>(), sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IEmbeddingService>()),
                new Vec0ReclaimJob(),
                new VacuumJob(),
                new MetricsRetentionJob(sp.GetRequiredService<TimeProvider>()),
                // ADR-0076: on-demand — HasWorkAsync reads the outbox itself, not a cadence.
                new ModelMigrationJob(sp.GetRequiredService<IEntryEmbedder>()),
                // ADR-0075 amendment: on-demand, same shape as ModelMigrationJob — HasWorkAsync reads
                // the repair_requests row a `repair <verb> --apply` submitted through the server.
                // Before PendingEmbedJob for the same reason ChunkBackfillJob is: a reingest repair
                // leaves rows pending, and PendingEmbedJob, last in this list, already sees them by
                // the time this same foreach reaches it.
                new ChunkIndexRepairJob(sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IEmbeddingService>(),
                    sp.GetRequiredService<TimeProvider>()),
                new ReingestRepairJob(sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IEmbeddingService>(),
                    sp.GetRequiredService<IMemoryStore>(), sp.GetRequiredService<TimeProvider>()),
                // Air-merge P2: the project-ids fold leaves renamed rows pending too, so it sits
                // with the other two repair relays, ahead of PendingEmbedJob.
                new ProjectIdsRepairJob(sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IEmbeddingService>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<ProjectIdsRepairJob>>()),
                // ADR-0075 amendment: on-demand, same shape as the two repair jobs above —
                // HasWorkAsync reads the promotion_queue_prune_requests row `extract prune --apply`
                // submitted through the server. Pure DELETE — never leaves anything pending for
                // PendingEmbedJob, so its position relative to it does not matter.
                new PromotionQueuePruneJob(sp.GetRequiredService<TimeProvider>()),
                // .NET-F1: on-demand — HasWorkAsync reads entries.embed_state itself, not a cadence.
                new PendingEmbedJob(sp.GetRequiredService<IEntryEmbedder>(), sp.GetRequiredService<IEventPump<EmbedDrainRequest>>()),
                // On-demand, same shape as PendingEmbedJob (docs/work/2026-08-21-code-search-implementation-plan.md
                // §3.8): signals a drain of code_entries rows a code-engine activation or
                // fingerprint change left pending. No outbox, no ToolGate interaction.
                new CodeReindexJob(sp.GetRequiredService<ICodeEmbedder>(), sp.GetRequiredService<IEventPump<EmbedDrainRequest>>())
            ]);
            services.AddHostedService<BankMaintenanceHostedService>();
        }

        /// <summary>
        ///     The embed topic's single background consumer. The <see cref="IEventPump{T}" />
        ///     singleton itself is registered by <see cref="RegisterFileIngestionServices" /> —
        ///     every producer (<see cref="PendingEmbedJob" />, <see cref="CodeReindexJob" />,
        ///     <see cref="WatchDigestExecutor" />, <see cref="FileIngestor" />,
        ///     <see cref="SqliteMemoryStore" />) needs it wherever <see cref="RegisterCoreMemoryServices" />
        ///     runs, including the narrower CLI graph that never registers this hosted service.
        /// </summary>
        private void RegisterEmbedDrainServices() => services.AddHostedService<EmbedDrainService>();

        /// <summary>
        ///     The capped buffer and its best-effort recorder alone (WP11): every graph that
        ///     constructs <see cref="EmbeddingService" /> or <see cref="SqliteMemoryStore" /> —
        ///     including the narrower CLI graph <see cref="RegisterCoreMemoryServices" /> builds on
        ///     its own — needs <see cref="IMeasurementRecorder" /> resolvable, whether or not that
        ///     process also runs the flusher that persists what it buffers. The buffer starts at the
        ///     documented default; MetricsFlusher applies the configured setting once at startup, so
        ///     nothing here blocks on a bank read.
        /// </summary>
        private void RegisterMeasurementRecording()
        {
            services.AddSingleton<IMeasurementBuffer>(_ => new MeasurementBuffer(MetricsConfigKeys.DefaultBufferCapacity));
            services.AddRequiredSingleton<IMeasurementRecorder, MetricsRecorder>();
        }

        /// <summary>
        ///     The persistence side alone (docs/plans/2026-08-15-performance-metrics-implementation.md,
        ///     WP3): the SQLite writer and the fixed-interval flusher, plus the read-side report
        ///     service. Server-only — <see cref="RegisterMeasurementRecording" /> is what the narrower
        ///     CLI graph needs, and it registers on its own from <see cref="RegisterCoreMemoryServices" />.
        /// </summary>
        private void RegisterMetricsServices()
        {
            services.AddRequiredSingleton<IMetricsStore, SqliteMetricsStore>();
            services.AddHostedService<MetricsFlusher>();
            services.AddRequiredSingleton<IMetricsReportService, MetricsReportService>();
        }

        /// <summary>
        ///     Also registers the embed topic's <see cref="IEventPump{T}" /> singleton (see
        ///     <see cref="RegisterEmbedDrainServices" />) — every caller of
        ///     <see cref="RegisterCoreMemoryServices" /> constructs <see cref="FileIngestor" /> and
        ///     <see cref="SqliteMemoryStore" />, both of which require it whether or not this
        ///     process also runs the hosted drain consumer.
        /// </summary>
        private void RegisterFileIngestionServices()
        {
            services.AddSingleton<IEventPump<EmbedDrainRequest>>(_ =>
                new EventPump<EmbedDrainRequest>(
                    new PumpTopic(EmbedDrainService.PumpCeiling, EmbedDrainService.PumpCapacity, true)));
            services.AddSingleton<O200kTokenizer>();
            services.AddSingleton<TokenCount>(sp => new TokenCount(sp.GetRequiredService<O200kTokenizer>().CountTokens));
            services.AddRequiredSingleton<IMarkdownChunker, MarkdownChunker>();
            services.AddSingleton<IJsonChunker>(sp => new JsonFileTypeChunker(
                sp.GetRequiredService<TokenCount>(),
                sp.GetRequiredService<IMarkdownChunker>(),
                ChunkingDefaults.OverlayTokens));
            services.AddSingleton<IFileTypeHandler>(sp => new MarkdownFileTypeHandler(sp.GetRequiredService<IMarkdownChunker>()));
            services.AddSingleton<IFileTypeHandler>(sp => new JsonFileTypeHandler(sp.GetRequiredService<IJsonChunker>()));
            services.AddSingleton<IReadOnlyCollection<IFileTypeHandler>>(sp => sp.GetServices<IFileTypeHandler>().ToList());
            services.AddRequiredSingleton<IIgnoreRulesProvider, IgnoreRulesProvider>();
            services.AddRequiredSingleton<IFileTypeMatcher, FileTypeMatcher>();

            // Code corpus (docs/work/2026-08-21-code-search-implementation-plan.md §3.4, WP2):
            // real line-range CodeChunker, budget 510 (#422: the model's measured window), counted with the bundled
            // code-daemon-embed-v1 sentencepiece tokenizer — the v1 default until a configured
            // code engine (embedding.codeModel, WP5) replaces the counting path. Always-ingest
            // per §3.3: code files chunk + store `pending` even with no engine configured.
            services.AddRequiredSingleton<ICodeFileTypeMatcher, CodeFileTypeMatcher>();
            services.AddRequiredSingleton<ICodeTokenizer, CodeTokenizer>();
            services.AddRequiredSingleton<ICodeChunker, CodeChunker>();
            services.AddRequiredSingleton<ICodeIngestor, CodeIngestor>();
            services.AddRequiredSingleton<IFileIngestor, FileIngestor>();
        }

        private void RegisterPromotionQueue()
        {
            services.AddRequiredSingleton<IEvictionPolicy, UniformCountEvictionPolicy>();
            services.AddRequiredSingleton<IPromotionQueueMetrics, PromotionQueueMetrics>();
            services.AddRequiredSingleton<IPromotionQueue, PromotionQueueService>();
        }

        private void RegisterStores()
        {
            services.AddSingleton(sp => new SqliteConnectionFactory(
                sp.GetRequiredService<InfrastructureOptions>(),
                sp.GetRequiredService<IEncryptionKeyResolver>(),
                sp.GetRequiredService<ILogger<SqliteConnectionFactory>>()));
            services.AddSingleton<ISqliteConnectionFactory>(sp => sp.GetRequiredService<SqliteConnectionFactory>());
            services.AddRequiredSingleton<IWatchStore, WatchStore>();
            // ADR-0075 amendment: the server-side default for `watch registered` — overridden by
            // LazyServerSettingsStore for the CLI graph, exactly like ISettingsStore above.
            services.AddSingleton<IWatchRegisteredStore>(sp => sp.GetRequiredService<WatchStore>());
            services.AddRequiredSingleton<INoiseFilteringService, NoiseFilteringService>();

            // Register default noise filter policies (deterministic only — see ADR-0033).
            services.AddSingleton<INoiseFilterPolicy, HermesProcessNoisePolicy>();

            // noise_entries (ADR-0029/ADR-0039): the training-data source — every rejected write,
            // TTL-purged by BankMaintenanceHostedService.
            services.AddRequiredSingleton<INoiseEntryStore, SqliteNoiseEntryStore>();
            // ADR-0075 amendment: the server-side default for `noise entries` — overridden by
            // LazyServerSettingsStore for the CLI graph, exactly like ISettingsStore above.
            services.AddSingleton<INoiseSummaryStore>(sp => sp.GetRequiredService<SqliteNoiseEntryStore>());

            // Self-learning noise substrate seam (ADR-0039, amended): no scoring model registered.
            // INoiseDetector -> NoOpNoiseDetector is what a future structural/lexical detector
            // plugs into once one is validated on held-out data.
            services.AddSingleton<INoiseDetector, NoOpNoiseDetector>();
            services.AddRequiredSingleton<INoiseShadowObserver, NoiseShadowObserver>();

            services.AddRequiredSingleton<IMemoryStore, SqliteMemoryStore>();
            // FTS5-only v1 leg (docs/work/2026-08-21-code-search-implementation-plan.md §3.6) —
            // the seam where a vec0 leg + RRF fusion would attach.
            services.AddRequiredSingleton<ICodeSearchService, SqliteCodeSearchService>();
            // ADR-0076: same instance as IMemoryStore — split out for the same reason ISettingsStore
            // was (ADR-0075), so the CLI can route model-set through the server independently.
            services.AddSingleton<IModelMigrationStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
            // ADR-0089: same instance as IMemoryStore, same reason as IModelMigrationStore above.
            // Server graph only — the CLI graph's wiring is carried to post-delta-5 §WP1.
            services.AddSingleton<IProjectRegistry>(sp => sp.GetRequiredService<SqliteMemoryStore>());
            // §3.3 D-E9: a store of its own, not another SqliteMemoryStore constructor parameter —
            // same ADR-0075 reason the two registrations above are split out.
            services.AddRequiredSingleton<ICodeEngineStore, SqliteCodeEngineStore>();
            services.AddRequiredSingleton<IMemorySourceStore, SqliteMemorySourceStore>();
            services.AddRequiredSingleton<ISettingsStore, SqliteSettingsStore>();
            // ADR-0075 amendment: the server-side default for `repair` — overridden by
            // LazyServerSettingsStore for the CLI graph, exactly like ISettingsStore above.
            services.AddRequiredSingleton<IRepairStore, SqliteRepairStore>();
            services.AddRequiredSingleton<IWorkspaceStore, SqliteWorkspaceStore>();
            services.AddRequiredSingleton<IPromotionQueueStore, SqlitePromotionQueueStore>();
            // ADR-0075 amendment: the server-side default for `extract prune` — overridden by
            // LazyServerSettingsStore for the CLI graph, exactly like ISettingsStore above.
            services.AddSingleton<IPromotionQueuePruneStore>(sp => sp.GetRequiredService<SqlitePromotionQueueStore>());
            // ADR-0075 amendment: the server-side default for `settings maintenance list`.
            services.AddRequiredSingleton<IMaintenanceStatsStore, SqliteMaintenanceStatsStore>();
        }

        private void RegisterEmbeddingServices()
        {
            services.AddRequiredSingleton<IBundledModel, BundledModel>();
            services.AddRequiredSingleton<ILocalTokenizer, LocalTokenizer>();
            services.AddRequiredSingleton<IEmbeddingService, EmbeddingService>();
            services.AddRequiredSingleton<ITokenizerFactory, EmbeddingTokenizerFactory>();
            services.AddRequiredSingleton<IRemoteDimensionProbe, RemoteDimensionProbe>();
            services.AddRequiredSingleton<IEmbeddingManifestSerializer, EmbeddingManifestSerializer>();
            services.AddRequiredSingleton<IEmbeddingManifestValidator, EmbeddingManifestValidator>();
            services.AddRequiredSingleton<IEmbeddingManifestLoader, EmbeddingManifestLoader>();
            services.AddRequiredSingleton<IManifestPoolingRepair, ManifestPoolingRepair>();
            services.AddRequiredSingleton<IModelDownloadPlanner, ModelDownloadPlanner>();
            services.AddRequiredSingleton<IOnnxGraphProbeReader, OnnxGraphProbeReader>();
            services.AddRequiredSingleton<ISentencePieceVocabularyReader, SentencePieceVocabularyReader>();
            services.AddRequiredSingleton<IOnnxSmokeTester, OrtOnnxSmokeTester>();
            services.AddRequiredSingleton<IDiskSpaceProvider, DiskSpaceProvider>();
            // ADR-0076: the migration lease EntryEmbedder's DrainMigrationAsync needs; registered
            // before IEntryEmbedder so constructor injection resolves it.
            services.AddRequiredSingleton<IModelMigrationLease, SqliteModelMigrationLease>();
            services.AddRequiredSingleton<IVecDimensionReconciler, VecDimensionReconciler>();
            // The drain's one log + metric surface (LANE P4): registered beside EntryEmbedder so
            // the narrower CLI graph RegisterCoreMemoryServices builds on its own can resolve it
            // too — EmbedDrainService (RegisterEmbedDrainServices) resolves the same singleton.
            services.AddSingleton<EmbedDrainReporter>();
            services.AddRequiredSingleton<IEntryEmbedder, EntryEmbedder>();
            services.AddRequiredSingleton<IEmbeddingAvailability, EmbeddingAvailability>();
            // The code corpus's own embedder (docs/work/2026-08-21-code-search-implementation-plan.md
            // §12.2 H5) — a second engine in the same keyed CreateGenerator cache, no
            // IModelMigrationLease (the code corpus has no outbox).
            services.AddRequiredSingleton<ICodeEmbedder, CodeEmbedder>();
        }

        /// <summary>Loops that only pay off in a long-lived host; a pure-stdio process is per-connection and recycled.</summary>
        private void RegisterLongLivedBackgroundServices(IReadOnlyCollection<McpTransport> mcpTransport)
        {
            if (!IsLongLivedHost(mcpTransport))
            {
                return;
            }

            services.AddHostedService<ExtractionHostedService>();
            services.AddHostedService<SweepHostedService>();
        }

        private void RegisterWatchServices()
        {
            services.AddRequiredSingleton<IWatchRetryPolicy, WatchRetryPolicy>();
            services.AddRequiredSingleton<IWatchOverlapResolver, WatchOverlapResolver>();
            services.AddRequiredSingleton<IWatchDigestExecutor, WatchDigestExecutor>();
            services.AddRequiredSingleton<IWatchScheduler, WatchScheduler>();
            services.AddRequiredSingleton<IWatchScanGuard, WatchScanGuard>();
            services.AddRequiredSingleton<IWatchScanLease, SqliteWatchScanLease>();
            services.AddRequiredSingleton<IWatchPipeline, WatchPipeline>();
            services.AddRequiredSingleton<IWatchService, WatchService>();
            services.RegisterWatchSyncBackgroundService();
        }

        private void RegisterWatchSyncBackgroundService()
        {
            services.AddSingleton<WatchCatchUp>();
            // WatchCatchUp implements IWatchScanInitiator; WatchDigestExecutor takes a Lazy<> of it
            // so its own construction never eagerly resolves WatchCatchUp — which needs
            // WatchPipeline, which needs IWatchDigestExecutor, which would otherwise cycle back
            // here (docs/work/2026-08-21-code-search-implementation-plan.md §5.3).
            services.AddSingleton<IWatchScanInitiator>(sp => sp.GetRequiredService<WatchCatchUp>());
            services.AddSingleton(sp => new Lazy<IWatchScanInitiator>(() => sp.GetRequiredService<IWatchScanInitiator>()));
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<WatchEventSource>>();
                return new WatchEventSource(sp.GetRequiredService<WatchPipeline>().Enqueue,
                    error => Log.WatchEventSourceError(logger, error.ProjectId, error.WatchPath, error.Message), logger);
            });
            services.AddHostedService<WatchHostedService>();
        }

        private void RegisterEncryptionServices(InfrastructureOptions options)
        {
            services.AddSingleton<ICliSecretManager>(_ => new BitwardenCliSecretManager());
            services.AddSingleton<IEncryptionSourceSidecar>(_ => new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)));
            services.AddSingleton<IEncryptionKeyProvider, NoneEncryptionKeyProvider>();
            services.AddSingleton<IEncryptionKeyProvider, EnvEncryptionKeyProvider>();
            services.AddSingleton<IEncryptionKeyProvider, BitwardenEncryptionKeyProvider>();
            services.AddSingleton<IEncryptionKeyResolver>(sp => new EncryptionKeyResolver(sp.GetRequiredService<IEncryptionSourceSidecar>(), [.. sp.GetServices<IEncryptionKeyProvider>()]));
        }
    }

    /// <summary>Transport that keeps the process alive past one client connection (HTTP/S).</summary>
    private static bool IsLongLivedHost(IReadOnlyCollection<McpTransport> mcpTransport) => mcpTransport.Contains(McpTransport.Http) || mcpTransport.Contains(McpTransport.Https);

    private static partial class Log
    {
        [LoggerMessage(EventId = 330, Level = LogLevel.Error,
            Message = "Watch event source error for {ProjectId} on {WatchPath}: {Message}")]
        public static partial void WatchEventSourceError(ILogger logger, string projectId, string watchPath,
            string message);
    }
}

public static class ServiceExtensions
{
    extension(IServiceCollection serviceCollection)
    {
        public void AddRequiredSingleton<TService, TImplementation>() where TImplementation : class, TService where TService : class =>
            serviceCollection.AddRequiredSingleton<TService, TImplementation>(sp => sp.GetRequiredService<TImplementation>());

        public void AddRequiredSingleton<TService, TImplementation>(Func<IServiceProvider, TImplementation> implementationFactory)
            where TImplementation : class, TService where TService : class
        {
            serviceCollection.AddSingleton<TImplementation>();
            serviceCollection.AddSingleton<TService, TImplementation>(implementationFactory);
        }
    }
}
