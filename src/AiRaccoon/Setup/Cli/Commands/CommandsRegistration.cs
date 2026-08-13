using AiRaccoon.Hosting.Node;

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
            serviceCollection.AddSingleton<ServeCommands>();
            serviceCollection.AddSingleton<ConfigCommands>();
            serviceCollection.RegisterNodeServices();
        }
    }
}
