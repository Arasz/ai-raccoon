using AiRaccoon.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using AiRaccoon.Core.Observability;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using AiRaccoon.Setup.Models;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;

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
            services.RegisterEmbeddingServices();
            services.RegisterFileIngestionServices();
            services.RegisterStores();
        }

        public void RegisterMemoryServices(InfrastructureOptions options, IReadOnlyCollection<McpTransport> mcpTransport)
        {
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
        }

        private void RegisterWorkspaceService() => services.AddSingleton<IWorkspaceService, WorkspaceService>();

        private void RegisterExtractionServices()
        {
            services.AddRequiredSingleton<ISharedExtractionService, SharedExtractionService>();
            services.AddRequiredSingleton<ISharedExtractionRunner, SharedExtractionRunner>();
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
            services.AddRequiredSingleton<IToolGate, ToolGate>();
        }

        private void RegisterObservabilityServices()
        {
            services.AddRequiredSingleton<ISearchQualityService, SqliteSearchQualityService>();
            services.AddSingleton<IOperationTelemetry, BackgroundTelemetry>();
            services.AddRequiredSingleton<IToolCallMetrics, ToolCallMetrics>();
        }

        private void RegisterBankMaintenanceBackgrounbdService()
        {
            services.AddRequiredSingleton<ISweepService, SweepService>();
            services.AddHostedService<BankMaintenanceHostedService>();
        }

        private void RegisterFileIngestionServices()
        {
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
            services.AddRequiredSingleton<IFileIngestor, FileIngestor>();
            services.AddRequiredSingleton<IFileTypeMatcher, FileTypeMatcher>();
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
                sp.GetRequiredService<IEncryptionKeyResolver>()));
            services.AddSingleton<ISqliteConnectionFactory>(sp => sp.GetRequiredService<SqliteConnectionFactory>());
            services.AddRequiredSingleton<IWatchStore, WatchStore>();
            services.AddRequiredSingleton<INoiseFilteringService, NoiseFilteringService>();

            // Register default noise filter policies (deterministic only — see ADR-0033).
            services.AddSingleton<INoiseFilterPolicy, HermesProcessNoisePolicy>();

            // noise_entries (ADR-0029/ADR-0039): the training-data source — every rejected write,
            // TTL-purged by BankMaintenanceHostedService.
            services.AddRequiredSingleton<INoiseEntryStore, SqliteNoiseEntryStore>();

            // Self-learning noise substrate seam (ADR-0039, amended): no scoring model registered.
            // INoiseDetector -> NoOpNoiseDetector is what a future structural/lexical detector
            // plugs into once one is validated on held-out data.
            services.AddSingleton<INoiseDetector, NoOpNoiseDetector>();
            services.AddRequiredSingleton<INoiseShadowObserver, NoiseShadowObserver>();

            services.AddRequiredSingleton<IMemoryStore, SqliteMemoryStore>();
            services.AddRequiredSingleton<IMemorySourceStore, SqliteMemorySourceStore>();
            services.AddRequiredSingleton<ISettingsStore, SqliteSettingsStore>();
            services.AddRequiredSingleton<IWorkspaceStore, SqliteWorkspaceStore>();
            services.AddRequiredSingleton<IPromotionQueueStore, SqlitePromotionQueueStore>();
        }

        private void RegisterEmbeddingServices()
        {
            services.AddRequiredSingleton<IBundledModel, BundledModel>();
            services.AddRequiredSingleton<IEmbeddingService, EmbeddingService>();
            services.AddRequiredSingleton<IEntryEmbedder, EntryEmbedder>();
            services.AddRequiredSingleton<IEmbeddingAvailability, EmbeddingAvailability>();
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
