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

    /// <summary>True when --transport names a removed value (stdio, https).</summary>
    public bool NamesRemovedTransport { get; init; }

    /// <summary>The Usage code a failed parse exits with: the most specific case its errors name.</summary>
    public int FailureCode =>
        IsUnparseable ? ErrorCode.Usage.Unparseable
        : NamesRemovedTransport ? ErrorCode.Usage.RemovedTransport
        : ParsedCliArgs.Errors.Any(error => error.SymbolResult is ArgumentResult { Tokens.Count: 0 }) ? ErrorCode.Usage.MissingValue
        : ParsedCliArgs.Errors.Any(error => error.SymbolResult is OptionResult { Option: var option } && option == CliCommandTree.ObservabilityPortOption)
            ? ErrorCode.Usage.UndialablePort
        : ErrorCode.Usage.InvalidValue;

    public ServerConfig ServerConfig { get; } = Options.ToServerConfig();
}
