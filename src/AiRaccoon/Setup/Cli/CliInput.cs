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

    /// <summary>
    ///     An unbound option token no command defines (a stray <c>--attach</c>): unparseable, exit 9
    ///     (ADR-0106 D4). A known option in the wrong place is a verb mistake and stays 15.
    /// </summary>
    public bool HasUnknownOption => ParsedCliArgs.UnmatchedTokens.Any(IsUnknownOption);

    private static bool IsUnknownOption(string token) =>
        token.StartsWith('-') && !CliCommandTree.KnownOptionNames.Contains(token.Split('=', ':')[0]);

    public ServerConfig ServerConfig { get; } = Options.ToServerConfig();
}
