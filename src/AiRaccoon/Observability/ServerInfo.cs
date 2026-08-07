namespace AiRaccoon.Observability;

/// <summary>Wire contract for GET /observability (ADR 0008): server identity, PID, and OTLP export state.</summary>
public sealed record ServerInfo(string Name, int Pid, OtlpInfo Otlp)
{
    private const string ServerName = "ai-raccoon";

    /// <summary>This process's identity: PID and current OTLP export state.</summary>
    public static ServerInfo Current() => new(ServerName, Environment.ProcessId, OtlpInfo.From(OtlpExportState.Resolve()));
}

/// <summary>OTLP export sub-object of <see cref="ServerInfo"/>.</summary>
public sealed record OtlpInfo(bool Enabled, string? Endpoint, string? Protocol)
{
    public static OtlpInfo From(OtlpExportState state) => new(state.Enabled, state.Endpoint, state.Protocol);
}
