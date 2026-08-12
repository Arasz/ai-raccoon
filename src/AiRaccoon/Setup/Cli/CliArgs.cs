using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using AiRaccoon.Setup.Cli.Options;

namespace AiRaccoon.Setup.Cli;

/// <summary>
///     The only type touching System.CommandLine: builds the verb-style config command tree
///     plus the launch-identity options, parses args, and renders help/errors/version. A verb
///     routes to ConfigCommands; no verb launches the MCP server. Secrets are never options.
/// </summary>
internal static class CliArgs
{
    private const string VersionOptionAction = "VersionOptionAction";
    private static readonly ParserConfiguration ParserConfiguration = new() { EnablePosixBundling = true };

    /// <summary>Parses args; never writes anything (stdout stays reserved for the stdio protocol).</summary>
    internal static bool TryParse(string[] args, out CliInput? result)
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse(args, ParserConfiguration);
        var errors = parseResult.Errors.Select(e => e.Message).ToList();
        var showHelp = parseResult.Action is HelpAction;
        var showVersion = parseResult.Action?.GetType().Name == VersionOptionAction;
        if (!showHelp && !showVersion && errors.Count > 0 && !ContainsVerb(args))
        {
            parseResult = CliCommandTree.BuildLaunchRootCommand().Parse(args, ParserConfiguration);
            errors = [.. parseResult.Errors.Select(e => e.Message)];
        }

        var commandPath = CommandPathOf(parseResult);
        var optionReadResult = ReadRootOptions(parseResult);
        if (optionReadResult.IsSuccess)
        {
            result = new CliInput(optionReadResult.Options, commandPath, showHelp, showVersion, errors, parseResult);
            return true;
        }

        result = new CliInput(RootCliOptions.Null, commandPath, showHelp, showVersion, [.. errors.Union(optionReadResult.Errors)], parseResult);
        return result is { ShowHelp: false, ShowVersion: false };
    }

    /// <summary>True when the args name one of the config verbs (skipping options and their values).</summary>
    private static bool ContainsVerb(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (!token.Contains('='))
                {
                    i++; // the option's value
                }

                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                continue; // -h / -? / bundled short options
            }

            if (CliCommandTree.Verbs.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Full verb path from the root, excluding the root itself; empty = run the server.</summary>
    private static string[] CommandPathOf(ParseResult parseResult)
    {
        var path = new List<string>();
        for (var current = parseResult.CommandResult; current is not null; current = current.Parent as CommandResult)
        {
            path.Insert(0, current.Command.Name);
        }

        return path.Count <= 1 ? [] : [.. path.Skip(1)];
    }

    private static OptionReadResult<RootCliOptions> ReadRootOptions(ParseResult result) =>
        result.ReadOptions((parseResult, collectedErrors) => new RootCliOptions
        {
            Transport = parseResult.ReadOption("--transport", DefaultOptions.Transport, collectedErrors),
            DataRoot = parseResult.ReadOption("--data-root", DefaultOptions.DataRoot, collectedErrors),
            InstallScope = parseResult.ReadOption("--install-scope", DefaultOptions.InstallScope, collectedErrors),
            Port = parseResult.ReadOption("--port", DefaultOptions.Port, collectedErrors),
            IsPortExplicit = parseResult.GetResult("--port") is OptionResult { Tokens.Count: > 0 },
            Quiet = parseResult.ReadOption("--quiet", false, collectedErrors, false)
        });
}
