using System.CommandLine;

namespace AiRaccoon.Setup.Cli.Render;

/// <summary>
///     Turns a parse result into CLI text: the Render loop (help/version/errors) plus the
///     shell-glob-expansion hint, which parses System.CommandLine error-message templates
///     pinned to 2.0.10 — on an SCL upgrade, update TryUnrecognizedToken/TryTypedArgumentToken.
/// </summary>
internal static class CliRendering
{
    /// <summary>
    ///     Renders help, version, or parse errors to the given writer and returns the exit
    ///     code (0 help/version, 1 parse errors); a shell-expanded '*' target appends a quoting
    ///     hint. The optional <paramref name="cwdEntries" /> overrides the real current-directory listing (tests only).
    /// </summary>
    internal static void Render(CliInput cliInput, StandardStreams streams, IReadOnlySet<string>? cwdEntries = null)
    {
        var exit = cliInput.ParsedCliArgs.Invoke(new InvocationConfiguration { Output = streams.Error, Error = streams.Error });
        if (exit == 0 && cliInput.Errors.Count > 0)
        {
            foreach (var message in cliInput.Errors)
            {
                streams.WriteErrorLine(message);
            }
        }

        if (cliInput.Errors.Count > 0 && GlobExpansionHint(cliInput, cwdEntries) is { } hint)
        {
            streams.WriteErrorLine(hint);
        }
    }

    /// <summary>
    ///     Detects a shell-expanded '*' target behind parse errors and returns a hint that
    ///     quotes the wildcard; null when the errors don't match the expansion signature.
    /// </summary>
    internal static string? GlobExpansionHint(CliInput result, IReadOnlySet<string>? cwdEntries = null)
    {
        if (result.Errors.Count == 0)
        {
            return null;
        }

        cwdEntries ??= CurrentDirectoryEntries();

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
            cwdHits >= 1 && cwdHits >= unrecognized.Count - 1 ||
            typedToken is not null && cwdEntries.Contains(typedToken) && unrecognized.Count >= 1;
        if (!expanded)
        {
            return null;
        }

        var nonCwdTokens = unrecognized.Where(token => !cwdEntries.Contains(token)).ToArray();
        if (result.CommandPath.Length >= 1 && nonCwdTokens.Length == 1)
        {
            return
                $"Hint: '*' was expanded by your shell into the files of this directory — quote it to target all projects, e.g.:\n  ai-raccoon {string.Join(' ', result.CommandPath)} '*' {nonCwdTokens[0]}";
        }

        return "Hint: '*' may have been expanded by your shell into the files of this directory — quote it as '*' to target all projects.";
    }

    private static IReadOnlySet<string> CurrentDirectoryEntries() =>
        Directory.GetFileSystemEntries(".")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

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
}
