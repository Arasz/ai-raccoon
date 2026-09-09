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

    /// <summary>Shared with CliRendering, which re-parses the resolved command path plus
    /// "--help" to render help for a recognised-but-incomplete command.</summary>
    internal static readonly ParserConfiguration ParserConfiguration = new() { EnablePosixBundling = true };

    /// <summary>Parses args; never writes anything (stdout stays reserved for the stdio protocol).</summary>
    internal static bool TryParse(string[] args, out CliInput? result)
    {
        var fullRoot = CliCommandTree.BuildFullRootCommand();
        var parseResult = fullRoot.Parse(args, ParserConfiguration);
        var errors = DistinctErrors(parseResult);
        var showHelp = parseResult.Action is HelpAction;
        var showVersion = parseResult.Action?.GetType().Name == VersionOptionAction;
        if (!showHelp && !showVersion && errors.Count > 0 && !ContainsVerb(args, fullRoot))
        {
            parseResult = CliCommandTree.BuildLaunchRootCommand().Parse(args, ParserConfiguration);
            errors = DistinctErrors(parseResult);
        }

        var commandPath = CommandPathOf(parseResult);
        var optionReadResult = ReadRootOptions(parseResult);
        if (!showHelp && !showVersion)
        {
            AppendTransportRemovalHint(args, errors);
        }

        if (optionReadResult.IsSuccess)
        {
            if (!showHelp && !showVersion)
            {
                AppendTransportRemovalRejection(args, errors, optionReadResult.Options);
            }

            result = new CliInput(optionReadResult.Options, commandPath, showHelp, showVersion, errors, parseResult);
            return true;
        }

        result = new CliInput(RootCliOptions.Null, commandPath, showHelp, showVersion, [.. errors.Union(optionReadResult.Errors)], parseResult);
        return false;
    }

    /// <summary>
    ///     P1 CLI-surface removal: --transport is proxy|http only. When the raw args name a
    ///     removed value, append one stderr line (still exit 9 for bare launches via the launch
    ///     Errors check, 15 for verb paths via ConfigCommands). Raw-args scan, so the hint does
    ///     not depend on how System.CommandLine bound the value — and keep-enum (ADR-0104) means
    ///     the members stay as parse-rejected values, so this scan is the mechanism, not a bridge
    ///     to a deletion.
    /// </summary>
    private static void AppendTransportRemovalHint(string[] args, List<string> errors)
    {
        if (TransportValue(args) is not { } value)
        {
            return;
        }

        if (string.Equals(value, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Hint: --transport stdio was removed; run bare 'ai-raccoon' for the proxy or 'ai-raccoon serve' for HTTP.");
        }
    }

    /// <summary>
    ///     Rejects removed values through the parsed enum (keep-enum, ADR-0104: Stdio/Https still
    ///     parse, so there is no coercion error to inherit): an explicit removed value adds one
    ///     line naming the allowed set. The raw spelling is echoed so --transport STDIO reads back
    ///     what was typed.
    /// </summary>
    private static void AppendTransportRemovalRejection(string[] args, List<string> errors, RootCliOptions options)
    {
        if (!options.IsTransportExplicit || options.Transport is not (McpTransport.Stdio or McpTransport.Https))
        {
            return;
        }

        var raw = TransportValue(args) ?? options.Transport.ToString().ToLowerInvariant();
        errors.Add($"Cannot parse argument '{raw}' as --transport: expected proxy|http.");
    }

    /// <summary>Raw --transport value, last occurrence wins (handles --transport v, --transport=v
    /// and --transport:v, case-preserved for the rejection echo).</summary>
    private static string? TransportValue(string[] args)
    {
        string? value = null;
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--transport=", StringComparison.Ordinal))
            {
                value = token["--transport=".Length..];
            }
            else if (token.StartsWith("--transport:", StringComparison.Ordinal))
            {
                value = token["--transport:".Length..];
            }
            else if (string.Equals(token, "--transport", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                var next = args[i + 1];
                if (!next.StartsWith("-", StringComparison.Ordinal))
                {
                    value = next;
                }
            }
        }

        return value;
    }

    /// <summary>System.CommandLine 2.0.10 reports some parse errors (e.g. a missing required
    /// argument) twice in one ParseResult.Errors — collapse to one message per distinct text
    /// so nothing downstream has to.</summary>
    private static List<string> DistinctErrors(ParseResult parseResult) => [.. parseResult.Errors.Select(e => e.Message).Distinct(StringComparer.Ordinal)];

    /// <summary>
    ///     True when the args name a top-level verb (skipping options and their values). The verb set
    ///     is read off the root that was just parsed, so there is no list to keep in step with the tree.
    /// </summary>
    private static bool ContainsVerb(string[] args, Command root)
    {
        var verbs = root.Children.OfType<Command>()
            .SelectMany(command => command.Aliases.Append(command.Name))
            .ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (!token.Contains('=') && !token.Contains(':'))
                {
                    i++; // the option's value
                }

                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                continue; // -h / -? / bundled short options
            }

            if (verbs.Contains(token))
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
            IsTransportExplicit = parseResult.GetResult("--transport") is OptionResult { Tokens.Count: > 0 },
            Quiet = parseResult.ReadOption("--quiet", false, collectedErrors, false)
        });
}
