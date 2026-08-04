using AiRaccoon.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Rating;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Setup;

public static partial class Dependencies
{
    public static void RegisterMemoryServices(this IServiceCollection services, InfrastructureOptions options)
    {
        // Sync credentials stay environment-only: layered here (the composition root),
        // never via CLI options and never in ServerConfig.Build. All other options are
        // pre-merged by ServerConfig.Build (CLI > env > default).
        options = options with
        {
            Sync = options.Sync with
            {
                AccessKey = Environment.GetEnvironmentVariable("AIRACCOON_SYNC_ACCESS_KEY"),
                SecretKey = Environment.GetEnvironmentVariable("AIRACCOON_SYNC_SECRET_KEY")
            }
        };

        services.AddSingleton(options);
        services.AddSingleton(options.Sync);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IEncryptionKeyProvider>(_ =>
            new EnvEncryptionKeyProvider());
        services.AddSingleton(sp => new SqliteConnectionFactory(
            sp.GetRequiredService<InfrastructureOptions>(),
            sp.GetRequiredService<IEncryptionKeyProvider>()));
        services.AddSingleton<EmbeddingService>();
        services.AddSingleton<SqliteMemoryStore>();
        services.AddSingleton<SqliteWorkspaceStore>();
        services.AddSingleton<IWorkspaceStore>(sp => sp.GetRequiredService<SqliteWorkspaceStore>());
        services.AddSingleton<IChunker, TokenizerChunker>();
        services.AddSingleton<MemoryExtensionHost>(sp => new MemoryExtensionHost(
            sp.GetRequiredService<SqliteMemoryStore>(),
            [sp.GetRequiredService<RetrievalRatingExtension>()]));
        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<MemoryExtensionHost>());
        services.AddSingleton<RetrievalRatingExtension>();
        services.AddSingleton<ICloudStore>(sp =>
        {
            var syncOpts = sp.GetRequiredService<SyncOptions>();
            if (!syncOpts.IsConfigured)
            {
                return new NullCloudStore();
            }

            return new S3CloudStore(syncOpts,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<S3CloudStore>());
        });
        services.AddSingleton(sp => new SyncService(
            sp.GetRequiredService<ICloudStore>(),
            async ct => await sp.GetRequiredService<SqliteConnectionFactory>().OpenBankAsync(ct),
            async (path, ct) =>
            {
                var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
                await conn.OpenAsync(ct);
                return conn;
            },
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SyncService>()));
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<SweepService>();
        services.AddSingleton<ForgettingPolicyService>();
        services.AddSingleton<IMemoryAccessGuard>(sp => new MemoryAccessGuard(
            sp.GetRequiredService<IMemoryStore>()));
        services.AddSingleton<ToolCallMetrics>();

        // Watch services resolve the same MemoryExtensionHost-decorated IMemoryStore, so
        // extension hooks (OnSourceChangedAsync) observe watcher digests. The hosted
        // service + catch-up/event-source registrations land in S5 (next wave).
        services.AddSingleton<WatchStore>();
        services.AddSingleton<IWatchStore>(sp => sp.GetRequiredService<WatchStore>());
        services.AddSingleton<WatchRetryPolicy>();
        services.AddSingleton<WatchDigestExecutor>();
        services.AddSingleton<WatchScheduler>();
        services.AddSingleton<WatchPipeline>();
        services.AddSingleton<IWatchService, WatchService>();

        // S5 watcher lifecycle: catch-up scans, the FileSystemWatcher adapter (adapter
        // failures surface as synthetic WatchEventError events — logged, never thrown),
        // and the hosted re-watch loop that starts/stops watchers on a poll.
        services.AddSingleton<WatchCatchUp>();
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<WatchEventSource>>();
            return new WatchEventSource(sp.GetRequiredService<WatchPipeline>().Enqueue,
                error => Log.WatchEventSourceError(logger, error.ProjectId, error.WatchPath, error.Message), logger);
        });
        services.AddHostedService<WatchHostedService>();
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 330, Level = LogLevel.Error,
            Message = "Watch event source error for {ProjectId} on {WatchPath}: {Message}")]
        public static partial void WatchEventSourceError(ILogger logger, string projectId, string watchPath,
            string message);
    }
}
