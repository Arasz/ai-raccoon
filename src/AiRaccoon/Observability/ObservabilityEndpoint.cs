namespace AiRaccoon.Observability;

/// <summary>Maps GET /observability (ADR 0008): server identity, PID, and OTLP export state.</summary>
internal static class ObservabilityEndpoint
{
    extension(WebApplication webApplication)
    {
        internal WebApplication MapObservability()
        {
            webApplication.MapGet("/observability", () => TypedResults.Ok(ServerInfo.Current()));
            return webApplication;
        }
    }
}
