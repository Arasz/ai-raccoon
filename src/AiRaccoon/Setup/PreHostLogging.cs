using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup;

/// <summary>
///     Builds a logger for the three call sites that need one before a host/DI container exists
///     (OtlpExport.AddOtlpExport, ServeRunner.RunAsync, ObservabilityRunner.RunAsync) — same
///     destination decision as HostLogging.Configure, wrapped in a throwaway factory the caller
///     disposes.
/// </summary>
internal static class PreHostLogging
{
    internal static Handle CreateLogger(string category, InfrastructureOptions options)
    {
        var factory = LoggerFactory.Create(b => HostLogging.Configure(b, [], options));
        return new Handle(factory, factory.CreateLogger(category));
    }

    internal readonly record struct Handle(ILoggerFactory Factory, ILogger Logger) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }
}
