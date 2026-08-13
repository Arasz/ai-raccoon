using System.CommandLine;
using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Setup.Cli;

/// <summary>
///     Parse outcome: options (null on help/version/errors), the verb command path (empty means
///     run the server), the help/version flags, the collected error messages, and the raw parse
///     result for rendering and value reads.
/// </summary>
public sealed record CliInput(
    RootCliOptions Options,
    string[] CommandPath,
    bool ShowHelp,
    bool ShowVersion,
    IReadOnlyList<string> Errors,
    ParseResult ParsedCliArgs)
{
    public bool IsCommandInput => CommandPath.Length > 0;

    public bool IsProxyInput => ServerConfig.Transport == McpTransport.Proxy;

    public ServerConfig ServerConfig { get; } = Options.ToServerConfig();
}
