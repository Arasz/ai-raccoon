using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup;

/// <summary>
///     Launch identity: transport + port + bank options (CLI only — env handling was removed by
///     the single-channel ruling; runtime configuration lives in the settings table via the
///     config commands).
/// </summary>
public sealed record ServerConfig(int Port, McpTransport Transport, InfrastructureOptions Options, TimeSpan IdleTimeout = default);
