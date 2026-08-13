using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;

namespace AiRaccoon.Hosting.Watchdog;

public static class WatchdogRegistrations
{
    extension(IServiceCollection serviceCollection)
    {
        public void RegisterWatchdogServices(ServerConfig serverConfig)
        {
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
