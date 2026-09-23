using System.CommandLine;
using System.CommandLine.Parsing;
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
    ///     The argv does not fit the command grammar — an unknown or misplaced token, a missing
    ///     subcommand — as opposed to a missing or invalid value for a slot it does fit.
    /// </summary>
    public bool IsUnparseable => ParsedCliArgs.Errors.Any(error => error.SymbolResult is CommandResult);

    public ServerConfig ServerConfig { get; } = Options.ToServerConfig();
}
