using System.Reflection;

namespace AiRaccoon.Observability;

/// <summary>Wire contract for GET /observability (ADR 0008): server identity, binary version,
/// PID, and OTLP export state. The version is what tells one build's server from another (ADR 0022).</summary>
public sealed record ServerInfo(string Name, string Version, int Pid, OtlpInfo Otlp)
{
    private const string ServerName = "ai-raccoon";

    private static readonly string BinaryVersion =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ServerInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>This process's identity: version, PID and current OTLP export state.</summary>
    public static ServerInfo Current() =>
        new(ServerName, BinaryVersion, Environment.ProcessId, OtlpInfo.From(OtlpExportState.Resolve()));
}

/// <summary>OTLP export sub-object of <see cref="ServerInfo"/>.</summary>
public sealed record OtlpInfo(bool Enabled, string? Endpoint, string? Protocol)
{
    public static OtlpInfo From(OtlpExportState state) => new(state.Enabled, state.Endpoint, state.Protocol);
}
