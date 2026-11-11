using AiRaccoon.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Decisions;
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
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Decisions;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
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
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Setup;

internal static partial class AppRegistrations
{
    extension(IServiceCollection services)
    {
        public void RegisterCoreMemoryServices(InfrastructureOptions options)
        {
            services.AddSingleton(options);
            services.AddHttpClient();
            services.AddSingleton(TimeProvider.System);
            services.AddRequiredSingleton<IDecisionStore, SqliteDecisionStore>();
            services.AddRequiredSingleton<IDecisionService, DecisionService>();
            services.RegisterEncryptionServices(options);

            services.AddSingleton<IIdentityProver>(static sp => new IdentityProver(
                sp.GetRequiredService<InfrastructureOptions>(),
                sp.GetRequiredService<IHttpClientFactory>()));

            services.RegisterMeasurementRecording();

            services.AddSingleton<IOperationTelemetry, BackgroundTelemetry>();
            services.RegisterEmbeddingServices();
            services.RegisterFileIngestionServices();

            services.AddRequiredSingleton<ISharedProjectScope, SharedProjectScope>();

            services.AddRequiredSingleton<IEntryKindClassifier, EntryKindClassifier>();
            services.RegisterStores();
        }

        public void RegisterMemoryServices(InfrastructureOptions options)
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
            services.RegisterLongLivedBackgroundServices();
            services.RegisterBankMaintenanceBackgroundService();
            services.RegisterMetricsServices();
        }

        private void RegisterWorkspaceService() => services.AddSingleton<IWorkspaceService, WorkspaceService>();

        private void RegisterExtractionServices()
        {
            services.AddRequiredSingleton<ISharedExtractionService, SharedExtractionService>();
            services.AddRequiredSingleton<ISharedExtractionRunner, SharedExtractionRunner>();
            services.AddRequiredSingleton<IShareExtractService, ShareExtractService>();
            services.AddRequiredSingleton<IQueryGuardService, QueryGuardService>();
            services.AddRequiredSingleton<IMemoryWriteService, MemoryWriteService>();
            services.AddRequiredSingleton<ISearchDispatcher, SearchDispatcher>();
        }

        private void RegisterSyncServices()
        {
            services.AddRequiredSingleton<ISyncCloudStoreFactory, SyncCloudStoreFactory>();
            services.AddSingleton<ISyncService>(static sp => new SyncService(
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
            services.AddRequiredSingleton<IProjectIdsMigrationGate, SqliteProjectIdsMigrationGate>();
            services.AddRequiredSingleton<IProjectRegistrationGuard, ProjectRegistrationGuard>();
            services.AddRequiredSingleton<ICwdProbe, CurrentDirectoryCwdProbe>();
            services.AddRequiredSingleton<IProjectIdResolver, CwdProjectIdResolver>();
            services.AddRequiredSingleton<IToolNameScope, ToolNameScope>();
            services.AddRequiredSingleton<IScopeCheck, ScopeCheck>();
            services.AddRequiredSingleton<IToolGate, ToolGate>();
        }

        private void RegisterObservabilityServices()
        {
            services.AddRequiredSingleton<ISearchQualityService, SqliteSearchQualityService>();
            services.AddRequiredSingleton<IToolCallMetrics, ToolCallMetrics>();
            services.AddSingleton<IBuildStamp>(new AssemblyBuildStamp(typeof(MemoryTools).Assembly));
        }

        private void RegisterBankMaintenanceBackgroundService()
        {
            services.AddRequiredSingleton<ISweepService, SweepService>();
            services.AddSingleton<MaintenanceJobRunner>();

            services.AddSingleton<IReadOnlyList<IMaintenanceJob>>(static sp =>
            [
                new ChunkBackfillJob(sp.GetRequiredService<IEntryKindClassifier>(), sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IMarkdownChunker>(),
                    sp.GetRequiredService<IPlainTextChunker>(), sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IEmbeddingService>()),

                new NoteChunkOrderRepairJob(sp.GetRequiredService<ILogger<NoteChunkOrderRepairJob>>()),

                new ChunkBoundaryRepairJob(sp.GetRequiredService<IEntryKindClassifier>(), sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IMarkdownChunker>(),
                    sp.GetRequiredService<IPlainTextChunker>(), sp.GetRequiredService<IEmbeddingService>(),
                    sp.GetRequiredService<IMemoryStore>(), sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<ChunkBoundaryRepairJob>>()),
                new Vec0ReclaimJob(),
                new VacuumJob(),
                new MetricsRetentionJob(sp.GetRequiredService<TimeProvider>()),
                new ModelMigrationJob(sp.GetRequiredService<IEntryEmbedder>()),
                new ChunkIndexRepairJob(sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IEmbeddingService>(),
                    sp.GetRequiredService<TimeProvider>()),
                new ReingestRepairJob(sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IEmbeddingService>(),
                    sp.GetRequiredService<IMemoryStore>(), sp.GetRequiredService<TimeProvider>()),
                new ProjectIdsRepairJob(sp.GetRequiredService<IFileTypeMatcher>(), sp.GetRequiredService<IEmbeddingService>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ISharedProjectScope>(),
                    sp.GetRequiredService<ILogger<ProjectIdsRepairJob>>()),

                new PromotionQueuePruneJob(sp.GetRequiredService<TimeProvider>()),

                new PendingEmbedJob(sp.GetRequiredService<IEntryEmbedder>(), sp.GetRequiredService<IEventPump<EmbedDrainRequest>>()),

                new CodeReindexJob(sp.GetRequiredService<ICodeEmbedder>(), sp.GetRequiredService<IEventPump<EmbedDrainRequest>>())
            ]);
            services.AddHostedService<BankMaintenanceHostedService>();
            services.AddHostedService<ProjectIdAliasCacheHostedService>();
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
            services.AddSingleton<IMeasurementBuffer>(static _ => new MeasurementBuffer(MetricsConfigKeys.DefaultBufferCapacity));
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
            services.AddSingleton<IEventPump<EmbedDrainRequest>>(static _ =>
                new EventPump<EmbedDrainRequest>(
                    new PumpTopic(EmbedDrainService.PumpCeiling, EmbedDrainService.PumpCapacity, true)));
            services.AddSingleton<O200kTokenizer>();
            services.AddSingleton<TokenCount>(static sp => sp.GetRequiredService<O200kTokenizer>().CountTokens);
            services.AddRequiredSingleton<IMarkdownChunker, MarkdownChunker>();
            services.AddRequiredSingleton<IPlainTextChunker, PlainTextChunker>();
            services.AddSingleton<IJsonChunker>(static sp => new JsonFileTypeChunker(
                sp.GetRequiredService<TokenCount>(),
                sp.GetRequiredService<IMarkdownChunker>(),
                ChunkingDefaults.OverlayTokens));
            services.AddSingleton<INotebookChunker>(static sp => new NotebookChunker(sp.GetRequiredService<IMarkdownChunker>()));
            services.AddSingleton<IFileTypeHandler>(static sp => new MarkdownFileTypeHandler(sp.GetRequiredService<IMarkdownChunker>()));
            services.AddSingleton<IFileTypeHandler>(static sp => new PlainTextFileTypeHandler(sp.GetRequiredService<IPlainTextChunker>()));
            services.AddSingleton<IFileTypeHandler>(static sp => new JsonFileTypeHandler(sp.GetRequiredService<IJsonChunker>()));
            services.AddSingleton<IFileTypeHandler>(static sp => new NotebookFileTypeHandler(sp.GetRequiredService<INotebookChunker>()));
            services.AddSingleton<IReadOnlyCollection<IFileTypeHandler>>(static sp => [.. sp.GetServices<IFileTypeHandler>()]);
            services.AddRequiredSingleton<IIgnoreRulesProvider, IgnoreRulesProvider>();
            services.AddRequiredSingleton<IFileTypeMatcher, FileTypeMatcher>();
            services.AddRequiredSingleton<ICodeFileTypeMatcher, CodeFileTypeMatcher>();
            services.AddRequiredSingleton<ICodeTokenizer, CodeTokenizer>();
            services.AddRequiredSingleton<ICodeChunker, CodeChunker>();
            services.AddRequiredSingleton<ICodeIngestor, CodeIngestor>();
            services.AddSingleton<IndexableFileWalk>();
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
            services.AddSingleton(static sp => new SqliteConnectionFactory(
                sp.GetRequiredService<InfrastructureOptions>(),
                sp.GetRequiredService<IEncryptionKeyResolver>(),
                sp.GetRequiredService<ILogger<SqliteConnectionFactory>>()));
            services.AddSingleton<ISqliteConnectionFactory>(static sp => sp.GetRequiredService<SqliteConnectionFactory>());
            services.AddRequiredSingleton<IWatchStore, WatchStore>();
            services.AddSingleton<IWatchRegisteredStore>(static sp => sp.GetRequiredService<WatchStore>());
            services.AddRequiredSingleton<INoiseFilteringService, NoiseFilteringService>();
            services.AddSingleton<INoiseFilterPolicy, HermesProcessNoisePolicy>();
            services.AddRequiredSingleton<INoiseEntryStore, SqliteNoiseEntryStore>();
            services.AddSingleton<INoiseSummaryStore>(static sp => sp.GetRequiredService<SqliteNoiseEntryStore>());
            services.AddSingleton<INoiseDetector, NoOpNoiseDetector>();
            services.AddRequiredSingleton<INoiseShadowObserver, NoiseShadowObserver>();
            services.AddRequiredSingleton<IMemoryStore, SqliteMemoryStore>();
            services.AddRequiredSingleton<ICodeSearchService, SqliteCodeSearchService>();
            services.AddSingleton<IModelMigrationStore>(static sp => sp.GetRequiredService<SqliteMemoryStore>());
            services.AddSingleton<IProjectRegistry>(static sp => sp.GetRequiredService<SqliteMemoryStore>());
            services.AddRequiredSingleton<IProjectDirectory, SqliteProjectDirectory>();
            services.AddRequiredSingleton<ICodeEngineStore, SqliteCodeEngineStore>();
            services.AddRequiredSingleton<IMemorySourceStore, SqliteMemorySourceStore>();
            services.AddRequiredSingleton<ISettingsStore, SqliteSettingsStore>();
            services.AddRequiredSingleton<IRepairStore, SqliteRepairStore>();
            services.AddRequiredSingleton<IWorkspaceStore, SqliteWorkspaceStore>();
            services.AddRequiredSingleton<IPromotionQueueStore, SqlitePromotionQueueStore>();
            services.AddSingleton<IPromotionQueuePruneStore>(static sp => sp.GetRequiredService<SqlitePromotionQueueStore>());
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
            services.AddRequiredSingleton<IModelMigrationLease, SqliteModelMigrationLease>();
            services.AddRequiredSingleton<IVecDimensionReconciler, VecDimensionReconciler>();
            services.AddSingleton<IChunkBudgetReconciler>(static sp => new ChunkBudgetReconciler(sp.GetRequiredService<IFileTypeMatcher>(),
                sp.GetRequiredService<IMarkdownChunker>(), sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IMemoryStore>,
                sp.GetRequiredService<ILogger<ChunkBudgetReconciler>>(),
                sp.GetRequiredService<IEntryKindClassifier>()));
            services.AddSingleton<EmbedDrainReporter>();
            services.AddRequiredSingleton<IEntryEmbedder, EntryEmbedder>();
            services.AddRequiredSingleton<IEmbeddingAvailability, EmbeddingAvailability>();
            services.AddRequiredSingleton<ICodeEmbedder, CodeEmbedder>();
        }

        /// <summary>Background loops for the long-lived web host — the sole surviving host shape, so always registered.</summary>
        private void RegisterLongLivedBackgroundServices()
        {
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
            services.AddSingleton<IWatchScanInitiator>(static sp => sp.GetRequiredService<WatchCatchUp>());
            services.AddSingleton(static sp => new Lazy<IWatchScanInitiator>(sp.GetRequiredService<IWatchScanInitiator>));
            services.AddSingleton(static sp =>
            {
                var logger = sp.GetRequiredService<ILogger<WatchEventSource>>();
                return new WatchEventSource(sp.GetRequiredService<WatchPipeline>().Enqueue,
                    error => Log.WatchEventSourceError(logger, error.ProjectId, error.WatchPath, error.Message), logger);
            });
            services.AddHostedService<WatchHostedService>();
        }

        private void RegisterEncryptionServices(InfrastructureOptions options)
        {
            services.AddSingleton<IEncryptionSourceSidecar>(_ => new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)));
            services.AddSingleton<IEncryptionKeyProvider, NoneEncryptionKeyProvider>();
            services.AddSingleton<IEncryptionKeyProvider, EnvEncryptionKeyProvider>();
            services.AddSingleton<IEncryptionKeyProvider>(static _ => new BitwardenEncryptionKeyProvider(new CliSecretManager(CliSecretTool.Bitwarden)));
            services.AddSingleton<IEncryptionKeyProvider>(static _ => new OnePasswordEncryptionKeyProvider(new CliSecretManager(CliSecretTool.OnePassword)));
            services.AddSingleton<IEncryptionKeyResolver>(static sp => new EncryptionKeyResolver(sp.GetRequiredService<IEncryptionSourceSidecar>(), [.. sp.GetServices<IEncryptionKeyProvider>()]));
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 330, Level = LogLevel.Error, Message = "Watch event source error for {ProjectId} on {WatchPath}: {Message}")]
        internal static partial void WatchEventSourceError(ILogger logger, string projectId, string watchPath, string message);
    }
}

internal static class ServiceExtensions
{
    extension(IServiceCollection serviceCollection)
    {
        public void AddRequiredSingleton<TService, TImplementation>() where TImplementation : class, TService where TService : class =>
            serviceCollection.AddRequiredSingleton<TService, TImplementation>(static sp => sp.GetRequiredService<TImplementation>());

        public void AddRequiredSingleton<TService, TImplementation>(Func<IServiceProvider, TImplementation> implementationFactory)
            where TImplementation : class, TService where TService : class
        {
            serviceCollection.AddSingleton<TImplementation>();
            serviceCollection.AddSingleton<TService, TImplementation>(implementationFactory);
        }
    }
}
