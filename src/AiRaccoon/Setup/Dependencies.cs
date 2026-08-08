using AiRaccoon.Core.Ingestion;
using AiRaccoon.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Tools;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Observability;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup;

public static partial class Dependencies
{
    extension(IServiceCollection services)
    {
        public void RegisterMemoryServices(InfrastructureOptions options, IReadOnlyCollection<McpTransport> mcpTransport)
        {
            services.AddSingleton(options);
            services.AddSingleton(TimeProvider.System);
            services.RegisterEncryptionServices(options);
            services.AddSingleton(sp => new SqliteConnectionFactory(
                sp.GetRequiredService<InfrastructureOptions>(),
                sp.GetRequiredService<IEncryptionKeyResolver>()));
            services.AddSingleton<SyncCloudStoreFactory>();
            services.AddHttpClient();
            services.AddSingleton<IBundledModel, BundledModel>();
            services.AddSingleton<EmbeddingAvailability>();
            services.AddSingleton<EmbeddingService>();
            services.AddSingleton<SqliteMemoryStore>();
            services.AddSingleton<SqliteWorkspaceStore>();
            services.AddSingleton<IWorkspaceStore>(sp => sp.GetRequiredService<SqliteWorkspaceStore>());
            services.AddSingleton<SqlitePromotionQueueStore>();
            services.AddSingleton<IPromotionQueueStore>(sp => sp.GetRequiredService<SqlitePromotionQueueStore>());
            services.AddSingleton<IEvictionPolicy, UniformCountEvictionPolicy>();
            services.AddSingleton<IPromotionQueueMetrics, PromotionQueueMetrics>();
            services.AddSingleton<IPromotionQueue, PromotionQueueService>();
            services.AddSingleton<IChunker, TokenizerChunker>();
            services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
            services.AddSingleton(sp => new SyncService(
                ct => sp.GetRequiredService<SyncCloudStoreFactory>().CreateAsync(ct),
                async ct => await sp.GetRequiredService<SqliteConnectionFactory>().OpenBankAsync(ct),
                (path, ct) => OpenSnapshotWithKey(sp, path, ct),
                (path, ct) => OpenSnapshotReadOnly(sp, path, ct),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SyncService>()));

            static async Task<SqliteConnection> OpenSnapshotWithKey(IServiceProvider sp, string path, CancellationToken ct)
            {
                // Snapshot files of an encrypted bank are encrypted copies — open them
                // with the bank key (null = unencrypted bank, plain open), read-write
                // (the strip DELETE + VACUUM) and with vec0 loaded (entry triggers).
                var conn = new SqliteConnection(SnapshotConnectionString(sp, path, readOnly: false));
                await conn.OpenAsync(ct);
                conn.EnableExtensions();
                conn.LoadVector();
                return conn;
            }

            static async Task<SqliteConnection> OpenSnapshotReadOnly(IServiceProvider sp, string path, CancellationToken ct)
            {
                var conn = new SqliteConnection(SnapshotConnectionString(sp, path, readOnly: true));
                await conn.OpenAsync(ct);
                conn.EnableExtensions();
                conn.LoadVector();
                return conn;
            }

            static string SnapshotConnectionString(IServiceProvider sp, string path, bool readOnly)
            {
                var key = sp.GetRequiredService<IEncryptionKeyResolver>().Resolve().Passphrase;
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

            services.AddSingleton<WorkspaceService>();
            services.AddSingleton<SweepService>();
            services.AddSingleton<SharedExtractionService>();
            services.AddSingleton<SharedExtractionRunner>();


            services.AddSingleton<ForgettingPolicyService>();
            services.AddSingleton<IMemoryAccessGuard>(sp => new MemoryAccessGuard(sp.GetRequiredService<IMemoryStore>()));
            services.AddSingleton<ToolGate>();
            services.AddSingleton<ToolCallMetrics>();

            services.RegisterWatchServices();

            services.RegisterExtractionBackgroundService(mcpTransport);
            services.RegisterWatchSyncBackgroundService();
            services.AddHostedService<BankMaintenanceHostedService>();
        }

        private void RegisterExtractionBackgroundService(IReadOnlyCollection<McpTransport> mcpTransport)
        {
            if (mcpTransport.Contains(McpTransport.Http) || mcpTransport.Contains(McpTransport.Https))
            {
                services.AddHostedService<ExtractionHostedService>();
            }
        }

        private void RegisterWatchServices()
        {
            services.AddSingleton<WatchStore>();
            services.AddSingleton<IWatchStore>(sp => sp.GetRequiredService<WatchStore>());
            services.AddSingleton<WatchRetryPolicy>();
            services.AddSingleton<WatchDigestExecutor>();
            services.AddSingleton<WatchScheduler>();
            // Singleton is load-bearing: a transient guard silently defeats single-flight (R8).
            services.AddSingleton<WatchScanGuard>();
            // Singleton is load-bearing here too: the owner id is per-instance, and a transient
            // lease would hand every call a new owner, so renew and release could never match.
            services.AddSingleton<IWatchScanLease, SqliteWatchScanLease>();
            services.AddSingleton<WatchPipeline>();
            services.AddSingleton<IWatchService, WatchService>();
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 330, Level = LogLevel.Error,
            Message = "Watch event source error for {ProjectId} on {WatchPath}: {Message}")]
        public static partial void WatchEventSourceError(ILogger logger, string projectId, string watchPath,
            string message);
    }
}
