using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Hosting.Node;

public static class NodeRegistration
{
    extension(IServiceCollection serviceCollection)
    {
        public void RegisterNodeServices()
        {
            serviceCollection.AddHttpClient(nameof(ServerProbe)).ConfigureHttpClient(client => client.Timeout = ServerProbe.RequestTimeout);
            serviceCollection.AddSingleton<ServerProbe>(sp => new ServerProbe(sp.GetRequiredService<IHttpClientFactory>()));
            serviceCollection.AddSingleton<IServerProbe>(sp => sp.GetRequiredService<ServerProbe>());
            serviceCollection.AddHttpClient(nameof(ObservabilityRunner)).ConfigureHttpClient(client => client.Timeout = ObservabilityRunner.RequestTimeout);
            serviceCollection.AddRequiredSingleton<IObservabilityRunner, ObservabilityRunner>();
            serviceCollection.AddHttpClient(nameof(ServerRestart))
                .ConfigurePrimaryHttpMessageHandler(_ => new SocketsHttpHandler { AllowAutoRedirect = false })
                .ConfigureHttpClient(c => c.Timeout = ServerRestart.RequestTimeout);
            serviceCollection.AddRequiredSingleton<IServerRestart, ServerRestart>(sp => new ServerRestart(
                sp.GetRequiredService<IServerProbe>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                ServerRestart.PortFreeWithin,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ServerRestart>>()));
            serviceCollection.AddRequiredSingleton<INodeRunner, NodeRunner>();
        }
    }
}
