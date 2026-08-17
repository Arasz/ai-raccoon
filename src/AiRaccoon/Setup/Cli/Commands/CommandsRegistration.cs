using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;

namespace AiRaccoon.Setup.Cli.Commands;

public static class CommandsRegistration
{
    extension(IServiceCollection serviceCollection)
    {
        public void RegisterCommands()
        {
            serviceCollection.AddSingleton<ExtractCommands>();
            serviceCollection.AddSingleton<SettingsCommands>();
            serviceCollection.AddSingleton<SyncCommands>();
            serviceCollection.AddSingleton<WatchCommands>();
            serviceCollection.AddSingleton<EncryptionCommands>();
            serviceCollection.AddSingleton<ExtractCommands>();
            serviceCollection.AddSingleton<MaintenanceCommands>();
            serviceCollection.AddSingleton<PerformanceCommands>();
            serviceCollection.AddSingleton<ServeCommands>();
            serviceCollection.AddSingleton<NoiseEntriesCommands>();
            serviceCollection.AddSingleton<ChunkIndexRepairCommands>();
            serviceCollection.AddSingleton(sp => new EncryptionCommands(
                sp.GetRequiredService<ISqliteConnectionFactory>(),
                sp.GetRequiredService<ICliSecretManager>(),
                sp.GetServices<IEncryptionKeyProvider>().OfType<EnvEncryptionKeyProvider>().Single(),
                sp.GetRequiredService<IEncryptionSourceSidecar>(),
                sp.GetRequiredService<ILogger<EncryptionCommands>>()));
            serviceCollection.AddSingleton<ConfigCommands>();
            serviceCollection.RegisterNodeServices();
        }
    }
}
