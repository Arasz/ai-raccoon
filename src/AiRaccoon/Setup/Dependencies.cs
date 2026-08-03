using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Provisioning;
using AiRaccoon.Infrastructure.Rating;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;

namespace AiRaccoon.Setup;

public static class Dependencies
{
    public static void RegisterMemoryServices(this IServiceCollection services)
    {
        // Options come from environment variables only — never hardcoded credentials.
        var scope = string.Equals(
            Environment.GetEnvironmentVariable("AIRACCOON_INSTALL_SCOPE"), "project",
            StringComparison.OrdinalIgnoreCase)
            ? InstallScope.Project
            : InstallScope.User;

        var options = new InfrastructureOptions
        {
            DataRoot = InfrastructureOptions.DefaultDataRoot(),
            Scope = scope,
            Sync = new SyncOptions
            {
                ManagedDatabaseId = Environment.GetEnvironmentVariable("AIRACCOON_SQLITECLOUD_DB_ID"),
                ApiKey = Environment.GetEnvironmentVariable("AIRACCOON_SQLITECLOUD_API_KEY")
            }
        };

        // Provision native extensions on first run (FR-MEM-1.19): download + verify the pinned
        // vector/memory/cloudsync modules for the host RID before any connection opens them.
        ProvisionExtensions(options);

        services.AddSingleton(options);
        services.AddSingleton(sp => new SqliteConnectionFactory(
            sp.GetRequiredService<InfrastructureOptions>(),
            true));
        services.AddSingleton<SqliteMemoryStore>();
        services.AddSingleton<MetaStore>();
        services.AddSingleton<IMemoryStore>(sp => new MemoryExtensionHost(
            sp.GetRequiredService<SqliteMemoryStore>(),
            [sp.GetRequiredService<RetrievalRatingExtension>()]));
        services.AddSingleton<RetrievalRatingExtension>();
        services.AddSingleton<ICloudSyncConnectionFactory, CloudSyncConnectionFactory>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<SweepService>();
    }

    private static void ProvisionExtensions(InfrastructureOptions options)
    {
        try
        {
            using var http = new HttpClient();
            var provisioner = new ExtensionProvisioner(
                options.DataRoot, options.Rid, http, ExtensionManifest.Sha256, true);
            provisioner.EnsureProvisionedAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // Do not crash the server on provisioning failure — the first tool call that opens the
            // bank will surface the precise missing-module error. Log to stderr (stdio-safe).
            Console.Error.WriteLine($"ai-raccoon: native extension provisioning failed: {exception.Message}");
        }
    }
}
