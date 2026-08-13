using System.Globalization;
using System.Text;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     Launch identity: transport + port + bank options (CLI only — env handling was removed by
///     the single-channel ruling; runtime configuration lives in the settings table via the
///     config commands).
/// </summary>
public sealed record ServerConfig(int Port, McpTransport Transport, InfrastructureOptions Options, TimeSpan IdleTimeout = default) : IIdleTimeoutProvider
{
    /// <summary>The loopback secret guarding /mcp; null leaves the endpoint ungated (ADR-0020).</summary>
    public string? McpToken { get; init; }

    /// <summary>
    ///     Prints the launch identity and never the token: for `serve` all logging goes to stderr, so
    ///     the synthesised ToString would put the loopback secret there on any future log line. A
    ///     property added later is omitted until it is listed here, which is the safe direction.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture,
            $"{nameof(Port)} = {Port}, {nameof(Transport)} = {Transport}, {nameof(Options)} = {Options}, ");
        builder.Append(CultureInfo.InvariantCulture, $"{nameof(IdleTimeout)} = {IdleTimeout}");
        return true;
    }
}
