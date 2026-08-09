using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using AiRaccoon.Infrastructure.Options;

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
    internal static bool TryParse(string[] args, out CliParseResult result)
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
        var options = ReadOptions(parseResult, out var optionErrors);
        var allErrors = errors.Union(optionErrors).ToList();
        result = new CliParseResult(options, commandPath, showHelp, showVersion, allErrors, parseResult);
        return result.Errors.Count <= 0 && result is { ShowHelp: false, ShowVersion: false };
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

    /// <summary>
    ///     Reads launch options, one at a time: a bad value on one option falls back to that
    ///     option's own default and is recorded in <paramref name="errors"/> — it can never
    ///     discard a sibling option that parsed fine (H1). System.CommandLine 2.0.10 does not
    ///     reliably surface a coercion failure via <c>parseResult.Errors</c> once a subcommand is
    ///     present (verified against the raw ParseResult); <see cref="OptionResult.GetValueOrDefault{T}"/>
    ///     throws <see cref="InvalidOperationException"/> instead, which this catches per option.
    /// </summary>
    private static CliOptions ReadOptions(ParseResult parseResult, out IReadOnlyList<string> errors)
    {
        var collectedErrors = new List<string>();
        var options = new CliOptions
        {
            Transport = ReadOption(parseResult, "--transport", DefaultOptions.Transport, collectedErrors),
            DataRoot = ReadOption(parseResult, "--data-root", DefaultOptions.DataRoot, collectedErrors),
            InstallScope = ReadOption(parseResult, "--install-scope", DefaultOptions.InstallScope, collectedErrors),
            Port = ReadOption(parseResult, "--port", DefaultOptions.Port, collectedErrors),
            IsPortExplicit = parseResult.GetResult("--port") is OptionResult { Tokens.Count: > 0 },
            Quiet = ReadOption(parseResult, "--quiet", false, collectedErrors, requireTokens: false)
        };
        errors = collectedErrors;
        return options;
    }

    /// <summary>Reads one option's value; a missing option (or one requiring tokens that has
    /// none) returns <paramref name="defaultValue"/>, and a coercion failure records its message
    /// in <paramref name="errors"/> and also returns <paramref name="defaultValue"/>.</summary>
    private static T ReadOption<T>(ParseResult parseResult, string optionName, T defaultValue, List<string> errors, bool requireTokens = true)
    {
        if (parseResult.GetResult(optionName) is not OptionResult result || (requireTokens && result.Tokens.Count == 0))
        {
            return defaultValue;
        }

        try
        {
            return result.GetValueOrDefault<T>();
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
            return defaultValue;
        }
    }
}
