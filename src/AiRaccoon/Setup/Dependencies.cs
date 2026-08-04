using AiRaccoon.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Rating;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;

namespace AiRaccoon.Setup;

public static partial class Dependencies
{
    public static void RegisterMemoryServices(this IServiceCollection services, InfrastructureOptions options)
    {
        services.AddSingleton(options);
        // Startup sync options are inert until the per-call settings re-resolution lands;
        // sync add/remove already write the settings rows the resolution will read.
        services.AddSingleton(new SyncOptions());
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
        services.AddSingleton<IMemoryStore>(sp => new MemoryExtensionHost(
            sp.GetRequiredService<SqliteMemoryStore>(),
            [sp.GetRequiredService<RetrievalRatingExtension>()]));
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
    }
}
