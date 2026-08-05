using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup.Cli;

namespace AiRaccoon.Setup;

/// <summary>Launch identity only: 3 nullable properties; null means the option was not given.</summary>
internal sealed record CliOptions(
    string? Transport,
    string? DataRoot,
    InstallScope? InstallScope);

/// <summary>
///     Parse outcome: options (null on help/version/errors), the verb command path (empty
///     means run the server), the help/version flags, the collected error messages, and the
///     raw parse result for rendering and value reads (help/errors go to the writer passed
///     to <see cref="CliArgs.Render"/> — never to stdout).
/// </summary>
internal sealed record CliParseResult(
    CliOptions? Options,
    string[] CommandPath,
    bool ShowHelp,
    bool ShowVersion,
    IReadOnlyList<string> Errors,
    ParseResult ParseResult);

/// <summary>
///     The only type touching System.CommandLine: builds the verb-style config command tree
///     plus the launch-identity options, parses args, and renders help/errors/version through
///     a caller-supplied writer. A verb routes to ConfigCommands; no verb launches the MCP
///     server. Secrets are never declared as options — the unknown-option parse error is the defense.
/// </summary>
internal static class CliArgs
{
    /// <summary>Parses args; never writes anything (stdout stays reserved for the stdio protocol).</summary>
    internal static CliParseResult Parse(string[] args)
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse(args, new ParserConfiguration { EnablePosixBundling = true });
        var errors = parseResult.Errors.Select(e => e.Message).ToList();
        var showHelp = parseResult.Action is HelpAction;
        // VersionOptionAction is a nested type with no public accessibility, so the
        // help/version idiom pins on its type name (pinned against 2.0.10).
        var showVersion = parseResult.Action?.GetType().Name == "VersionOptionAction";
        if (!showHelp && !showVersion && errors.Count > 0 && !ContainsVerb(args))
        {
            // No verb intended: re-parse against the launch-only root so bare
            // `ai-raccoon [--transport|--data-root|--install-scope]` runs the server
            // instead of failing on the required-subcommand rule.
            parseResult = CliCommandTree.BuildLaunchRootCommand().Parse(args, new ParserConfiguration { EnablePosixBundling = true });
            errors = [.. parseResult.Errors.Select(e => e.Message)];
        }

        var commandPath = CommandPathOf(parseResult);
        var options = showHelp || showVersion || errors.Count > 0 ? null : ReadOptions(parseResult);
        return new CliParseResult(options, commandPath, showHelp, showVersion, errors, parseResult);
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

    /// <summary>
    ///     Renders help, version, or parse errors to the given writer and returns the exit
    ///     code (0 help/version, 1 parse errors). Program.cs passes Console.Error.
    /// </summary>
    internal static int Render(CliParseResult result, TextWriter output, IReadOnlySet<string>? cwdEntries = null)
        => CliRendering.Render(result, output, cwdEntries);

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

    private static CliOptions? ReadOptions(ParseResult parseResult)
    {
        var transport = OptionValue(parseResult, "--transport", r => r.GetValueOrDefault<McpTransport>().ToString().ToLowerInvariant());
        var dataRoot = OptionValue(parseResult, "--data-root", r => r.GetValueOrDefault<string>());
        var scope = InstallScopeValue(parseResult);

        if (transport is null && dataRoot is null && scope is null)
        {
            return null;
        }

        return new CliOptions(transport, dataRoot, scope);
    }

    private static T? OptionValue<T>(ParseResult parseResult, string name, Func<OptionResult, T> read) => parseResult.GetResult(name) is OptionResult result ? read(result) : default;

    private static InstallScope? InstallScopeValue(ParseResult parseResult) =>
        parseResult.GetResult("--install-scope") is OptionResult result
            ? result.GetValueOrDefault<InstallScope>()
            : null;

}
