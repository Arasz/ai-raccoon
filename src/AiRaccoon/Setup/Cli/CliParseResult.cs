using System.CommandLine;

namespace AiRaccoon.Setup.Cli;

/// <summary>
///     Parse outcome: options (null on help/version/errors), the verb command path (empty means
///     run the server), the help/version flags, the collected error messages, and the raw parse
///     result for rendering and value reads.
/// </summary>
internal sealed record CliParseResult(
    CliOptions Options,
    string[] CommandPath,
    bool ShowHelp,
    bool ShowVersion,
    IReadOnlyList<string> Errors,
    ParseResult ParseResult);
