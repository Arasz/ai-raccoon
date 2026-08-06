using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup.Cli;

/// <summary>Launch identity only: 3 nullable properties (null means the option was not given) plus the port.</summary>
public sealed record CliOptions
{
    public required McpTransport Transport { get; init; }
    public required string DataRoot { get; init; }
    public required InstallScope InstallScope { get; init; }
    public required int Port { get; init; }
    public required bool IsPortExplicit { get; init; }
    public bool Quiet { get; init; }
}
