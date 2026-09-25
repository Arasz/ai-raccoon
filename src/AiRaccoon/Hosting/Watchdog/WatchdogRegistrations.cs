using AiRaccoon.Core.Observability;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;

namespace AiRaccoon.Hosting.Watchdog;

public static class WatchdogRegistrations
{
    extension(IServiceCollection serviceCollection)
    {
        public void RegisterWatchdogServices(ServerConfig serverConfig)
        {
            // Unconditional, unlike IdleWatchdog below: every serve host is a `dotnet tool update`
            // target regardless of --idle-timeout (ADR-0116).
            serviceCollection.AddHostedService(sp => new InstallWatchdog(
                AppContext.BaseDirectory,
                () => Directory.Exists(AppContext.BaseDirectory),
                InstallWatchdog.DefaultCheckInterval,
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<IOperationTelemetry>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<InstallWatchdog>>()));

            if (serverConfig.IdleTimeout <= TimeSpan.Zero)
            {
                return;
            }

            serviceCollection.AddSingleton<IIdleTimeoutProvider>(serverConfig);
            serviceCollection.AddSingleton(typeof(TimeSpan), serverConfig.IdleTimeout);
            serviceCollection.AddRequiredSingleton<IActivitySignaler, IdleWatchdog>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());
        }
    }
}
