using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;

namespace AiRaccoon.Hosting.Node;

public static class NodeRegistration
{
    extension(IServiceCollection serviceCollection)
    {
        public void RegisterNodeServices()
        {
            // The per-attempt bound belongs to the probe's retry pipeline. A client timeout here races it
            // and throws TaskCanceledException whose INNER type is TimeoutException — Polly matches the
            // outer, so the retry would never fire in production even though it does in tests.
            serviceCollection.AddHttpClient(nameof(ServerProbe)).RemoveAllLoggers().ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
            serviceCollection.AddSingleton<ServerProbe>(sp => new ServerProbe(sp.GetRequiredService<IHttpClientFactory>()));
            serviceCollection.AddSingleton<IServerProbe>(sp => sp.GetRequiredService<ServerProbe>());
            serviceCollection.AddHttpClient(nameof(ObservabilityRunner)).RemoveAllLoggers().ConfigureHttpClient(client => client.Timeout = ObservabilityRunner.RequestTimeout);
            serviceCollection.AddRequiredSingleton<IObservabilityRunner, ObservabilityRunner>();
            serviceCollection.AddHttpClient(nameof(ServerRestart))
                .RemoveAllLoggers()
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
