namespace AiRaccoon.Observability;

/// <summary>Maps GET /observability (ADR 0008): server identity, PID, and OTLP export state.</summary>
internal static class ObservabilityEndpoint
{
    public const string Path = "/observability";

    extension(WebApplication webApplication)
    {
        internal WebApplication MapObservability()
        {
            webApplication.MapGet(Path, () => TypedResults.Ok(ServerInfo.Current()));
            return webApplication;
        }
    }
}
