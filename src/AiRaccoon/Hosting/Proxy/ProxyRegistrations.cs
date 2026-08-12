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
            serviceCollection.AddHttpClient(nameof(ServerProbe)).ConfigureHttpClient(client => client.Timeout = ServerProbe.RequestTimeout);
            serviceCollection.AddRequiredSingleton<IServerProbe, ServerProbe>();
            serviceCollection.AddHttpClient(nameof(BackendSessions)).ConfigurePrimaryHttpMessageHandler(_ => new JsonRpcErrorHandler
            {
                InnerHandler = new SocketsHttpHandler()
            }).ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
            serviceCollection.AddRequiredSingleton<IBackendLauncher, BackendLauncher>();
            serviceCollection.AddRequiredSingleton<IProxyRunner, ProxyRunner>();
            serviceCollection.AddRequiredSingleton<IProxyForwarder, ProxyForwarder>();
        }
    }
}
