using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.IO;
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
///     server. Secrets are never declared as options — the unknown-option parse error is the
///     defense (they move through settings via the config commands' documented options).
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
    ///     code (0 help/version, 1 parse errors). Program.cs passes Console.Error. When the
    ///     errors match a shell-expanded '*' target, a quoting hint is appended. The
    ///     optional <paramref name="cwdEntries"/> overrides the real current-directory
    ///     listing (tests only).
    /// </summary>
    internal static int Render(CliParseResult result, TextWriter output, IReadOnlySet<string>? cwdEntries = null)
    {
        var exit = result.ParseResult.Invoke(new InvocationConfiguration { Output = output, Error = output });
        if (result.Errors.Count > 0 && GlobExpansionHint(result, cwdEntries) is { } hint)
        {
            output.WriteLine(hint);
        }

        return exit;
    }

    /// <summary>
    ///     Detects a shell-expanded '*' target behind parse errors and returns a hint that
    ///     quotes the wildcard; null when the errors don't match the expansion signature.
    /// </summary>
    internal static string? GlobExpansionHint(CliParseResult result, IReadOnlySet<string>? cwdEntries = null)
    {
        if (result.Errors.Count == 0)
        {
            return null;
        }

        cwdEntries ??= Directory.GetFileSystemEntries(".")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

        var unrecognized = new List<string>();
        string? typedToken = null;
        foreach (var error in result.Errors)
        {
            if (TryUnrecognizedToken(error, out var token))
            {
                unrecognized.Add(token);
            }
            else if (typedToken is null && TryTypedArgumentToken(error, out var typed))
            {
                typedToken = typed;
            }
        }

        var cwdHits = unrecognized.Count(token => cwdEntries.Contains(token));
        var expanded =
            (cwdHits >= 1 && cwdHits >= unrecognized.Count - 1) ||
            (typedToken is not null && cwdEntries.Contains(typedToken) && unrecognized.Count >= 1);
        if (!expanded)
        {
            return null;
        }

        var nonCwdTokens = unrecognized.Where(token => !cwdEntries.Contains(token)).ToArray();
        if (result.CommandPath.Length >= 1 && nonCwdTokens.Length == 1)
        {
            return $"Hint: '*' was expanded by your shell into the files of this directory — quote it to target all projects, e.g.:\n  ai-raccoon {string.Join(' ', result.CommandPath)} '*' {nonCwdTokens[0]}";
        }

        return "Hint: '*' may have been expanded by your shell into the files of this directory — quote it as '*' to target all projects.";
    }

    private static bool TryUnrecognizedToken(string message, out string token)
    {
        const string prefix = "Unrecognized command or argument '";
        if (message.StartsWith(prefix, StringComparison.Ordinal) && message.EndsWith("'.", StringComparison.Ordinal))
        {
            token = message[prefix.Length..^2];
            return true;
        }

        token = "";
        return false;
    }

    private static bool TryTypedArgumentToken(string message, out string token)
    {
        token = "";
        if (!message.Contains("as expected type 'System.Boolean'", StringComparison.Ordinal) &&
            !message.Contains("as expected type 'System.Int32'", StringComparison.Ordinal))
        {
            return false;
        }

        const string prefix = "Cannot parse argument '";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += prefix.Length;
        var end = message.IndexOf('\'', start);
        if (end < 0)
        {
            return false;
        }

        token = message[start..end];
        return true;
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
