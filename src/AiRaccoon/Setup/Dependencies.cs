using AiRaccoon.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Watch;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Rating;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup;

public static partial class Dependencies
{
    public static void RegisterMemoryServices(this IServiceCollection services, InfrastructureOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBwsProcessRunner>(_ => new BwsProcessRunner());
        services.AddSingleton<EncryptionKeyResolver>(sp => new EncryptionKeyResolver(
            sp.GetRequiredService<InfrastructureOptions>(),
            new EnvEncryptionKeyProvider(),
            sp.GetRequiredService<IBwsProcessRunner>()));
        services.AddSingleton<IEncryptionKeyProvider>(sp => sp.GetRequiredService<EncryptionKeyResolver>());
        services.AddSingleton(sp => new SqliteConnectionFactory(
            sp.GetRequiredService<InfrastructureOptions>(),
            sp.GetRequiredService<IEncryptionKeyProvider>()));
        services.AddSingleton<SyncCloudStoreFactory>();
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
        services.AddSingleton(sp => new SyncService(
            ct => sp.GetRequiredService<SyncCloudStoreFactory>().CreateAsync(ct),
            async ct => await sp.GetRequiredService<SqliteConnectionFactory>().OpenBankAsync(ct),
            async (path, ct) =>
            {
                var conn = new SqliteConnection($"Data Source={path}");
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
