using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;

namespace AiRaccoon.Hosting.Node;

public static class NodeRegistration
{
    extension(IServiceCollection serviceCollection)
    {
        public void RegisterNodeServices()
        {
            serviceCollection.AddHttpClient(nameof(ServerProbe)).ConfigureHttpClient(client => client.Timeout = ServerProbe.RequestTimeout);
            serviceCollection.AddRequiredSingleton<IServerProbe, ServerProbe>();
            serviceCollection.AddHttpClient(nameof(ObservabilityRunner)).ConfigureHttpClient(client => client.Timeout = ObservabilityRunner.RequestTimeout);
            serviceCollection.AddRequiredSingleton<IObservabilityRunner, ObservabilityRunner>();
            serviceCollection.AddHttpClient(nameof(ServerRestart))
                .ConfigurePrimaryHttpMessageHandler(_ => new SocketsHttpHandler { AllowAutoRedirect = false })
                .ConfigureHttpClient(c => c.Timeout = ServerRestart.RequestTimeout);
            serviceCollection.AddRequiredSingleton<IServerRestart, ServerRestart>();
            serviceCollection.AddRequiredSingleton<INodeRunner, NodeRunner>();
        }
    }
}
