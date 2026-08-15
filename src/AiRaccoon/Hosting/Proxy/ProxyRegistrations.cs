using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Setup;

namespace AiRaccoon.Hosting.Proxy;

public static class ProxyRegistrations
{
    extension(IServiceCollection serviceCollection)
    {
        public void RegisterProxyServices()
        {
            serviceCollection.AddSingleton(TimeProvider.System);
            serviceCollection.AddHttpClient(nameof(ServerProbe)).RemoveAllLoggers().ConfigureHttpClient(client => client.Timeout = ServerProbe.RequestTimeout);
            serviceCollection.AddSingleton<ServerProbe>(sp => new ServerProbe(sp.GetRequiredService<IHttpClientFactory>()));
            serviceCollection.AddSingleton<IServerProbe>(sp => sp.GetRequiredService<ServerProbe>());
            serviceCollection.AddHttpClient(BackendLauncher.BackendSessionClient).ConfigurePrimaryHttpMessageHandler(_ => new JsonRpcErrorHandler
            {
                // ADR-0022:135-137: .NET strips Authorization across a host hop but not a custom
                // header, so a redirect would carry the loopback token off-machine.
                InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false }
            }).ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
            serviceCollection.AddRequiredSingleton<IBackendLauncher, BackendLauncher>(sp => new BackendLauncher(
                sp.GetRequiredService<IServerProbe>(),
                BackendLauncher.DefaultBudget,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<BackendLauncher>>()));
            serviceCollection.AddRequiredSingleton<IProxyRunner, ProxyRunner>();
            serviceCollection.AddRequiredSingleton<IProxyForwarder, ProxyForwarder>();
        }
    }
}
