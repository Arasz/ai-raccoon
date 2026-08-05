using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.IO;
using AiRaccoon.Infrastructure.Options;

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
    private const string Description = "MCP server exposing agent memory over sqlite-memory";

    private static readonly string[] Verbs = ["access", "model", "retrieval", "sweep", "sync", "watch", "encryption"];

    /// <summary>The full tree: launch flags + verb commands (help rendered from this root shows the verbs).</summary>
    internal static RootCommand BuildFullRootCommand()
    {
        var root = new RootCommand(Description);
        AddLaunchOptions(root);
        root.Add(AccessCommand());
        root.Add(ModelCommand());
        root.Add(RetrievalCommand());
        root.Add(SweepCommand());
        root.Add(SyncCommand());
        root.Add(WatchCommand());
        root.Add(EncryptionCommand());
        return root;
    }

    /// <summary>Launch-only root for bare server invocations (no verb): System.CommandLine
    /// treats a root with subcommands as requiring one, so verb-less flag sets re-parse here.</summary>
    private static RootCommand BuildLaunchRootCommand()
    {
        var root = new RootCommand(Description);
        AddLaunchOptions(root);
        return root;
    }

    private static void AddLaunchOptions(RootCommand root)
    {
        root.Add(new Option<McpTransport>("--transport") { Description = "MCP transport; https unsupported", HelpName = "stdio|http|https" });
        root.Add(new Option<string>("--data-root") { Description = "Bank data root (must precede the verb)", HelpName = "path" });
        root.Add(new Option<InstallScope>("--install-scope") { Description = "Install scope (must precede the verb)", HelpName = "user|project" });
        // WebApplicationFactory bootstraps the entry point with these host-config flags;
        // declared hidden so the E2E host builds — values are intentionally never consumed
        // (CreateBuilder([]) drops generic host flags by design).
        root.Add(new Option<string>("--environment") { Hidden = true });
        root.Add(new Option<string>("--contentRoot") { Hidden = true });
        root.Add(new Option<string>("--applicationName") { Hidden = true });
    }

    /// <summary>Parses args; never writes anything (stdout stays reserved for the stdio protocol).</summary>
    internal static CliParseResult Parse(string[] args)
    {
        var parseResult = BuildFullRootCommand().Parse(args, new ParserConfiguration { EnablePosixBundling = true });
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
            parseResult = BuildLaunchRootCommand().Parse(args, new ParserConfiguration { EnablePosixBundling = true });
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

            if (Verbs.Contains(token))
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

    private static Command AccessCommand()
    {
        var access = new Command("access", "Access-mode configuration (per-project overrides the global default)");
        var defaultCmd = new Command("default", "Global default access mode")
        {
            new Command("set", "Sets the global default") { new Argument<string>("mode") { HelpName = "ro|rw|full" } },
            new Command("show", "Shows the effective global default (row value, else rw)")
        };
        access.Add(defaultCmd);
        access.Add(new Command("set", "Sets a per-project override; '*' targets the global default")
            { new Argument<string>("project-id") { HelpName = "project-id|*" }, new Argument<string>("mode") { HelpName = "ro|rw|full" } });
        access.Add(new Command("unset", "Drops a per-project override (falls back to the default)") { new Argument<string>("project-id") { HelpName = "project-id|*" } });
        access.Add(new Command("list", "Lists the default and every override"));
        return access;
    }

    private static Command ModelCommand()
    {
        var model = new Command("model", "Embedding engine configuration");
        var set = new Command("set", "Sets the embedding engine")
        {
            new Command("local", "Embeds in-process with the bundled ONNX model; optional path overrides it") { new Argument<string?>("path") { HelpName = "path", Arity = ArgumentArity.ZeroOrOne } },
            new Command("openai", "Routes through an OpenAI-compatible endpoint; key via --api-key (persisted in settings)")
            {
                new Argument<string>("model") { HelpName = "model-id" }, new Argument<string?>("base-url") { HelpName = "url", Arity = ArgumentArity.ZeroOrOne },
                new Option<string>("--api-key") { Description = "API key persisted in the settings table", HelpName = "key" }
            }
        };
        model.Add(set);
        model.Add(new Command("reset", "Back to default: no engine (FTS5-only search)"));
        model.Add(new Command("show", "Shows the configured provider/model/baseUrl/engine"));
        return model;
    }

    private static Command RetrievalCommand()
    {
        var retrieval = new Command("retrieval", "Retrieval configuration");
        var alpha = new Command("alpha", "Dual-vector fusion alpha")
        {
            new Command("set", "Sets retrieval.structureAlpha (0..1, default 0.5)") { new Argument<string>("alpha") { HelpName = "0..1" } },
            new Command("show", "Shows the current alpha (row value, else 0.5)")
        };
        retrieval.Add(alpha);
        return retrieval;
    }

    private static Command SweepCommand()
    {
        var sweep = new Command("sweep", "Degradation sweep configuration (ttl_days was removed by ruling)");
        var threshold = new Command("threshold", "Sweep rating threshold")
        {
            new Command("set", "Sets sweep.threshold (0..1, default 0.3)") { new Argument<string>("threshold") { HelpName = "0..1" } }
        };
        sweep.Add(threshold);
        sweep.Add(new Command("show", "Shows the current threshold (row value, else 0.3)"));
        return sweep;
    }

    private static Command SyncCommand()
    {
        var sync = new Command("sync", "Cloud sync configuration");
        var add = new Command("add", "Adds cloud sync");
        var s3 = new Command("s3", "S3-compatible endpoint (credentials are persisted in the settings table, or use --cli for the AWS credential chain)")
        {
            new Argument<string>("url") { HelpName = "url" },
            new Option<string>("--bucket") { Description = "S3 bucket name", HelpName = "name", Required = true },
            new Option<string>("--region") { Description = "S3 region", HelpName = "name" },
            new Option<string>("--object-key") { Description = "S3 object key (default memory-<projectId>.db)", HelpName = "key" },
            new Option<bool>("--cli") { Description = "Use the AWS default credential chain (aws configure / aws sso login); no key prompts" }
        };
        add.Add(s3);
        var azure = new Command("azure", "Azure Blob container (connection string prompted, or --cli with DefaultAzureCredential)")
        {
            new Argument<string>("container") { HelpName = "name" },
            new Option<string>("--object-key") { Description = "Azure blob name (default memory-<projectId>.db)", HelpName = "key" },
            new Option<bool>("--cli") { Description = "Use DefaultAzureCredential (az login); no connection-string prompt" },
            new Option<string>("--account") { Description = "Azure storage account name (required with --cli)", HelpName = "name" }
        };
        add.Add(azure);
        sync.Add(add);
        sync.Add(new Command("remove", "Back to default: sync off"));
        sync.Add(new Command("show", "Shows the sync configuration (keys redacted)"));
        return sync;
    }

    private static Command EncryptionCommand()
    {
        var encryption = new Command("encryption", "Bank encryption source configuration");
        var bitwarden = new Command("bitwarden",
            "Configures Bitwarden Secrets Manager as the bank key source (interactive ids; rekeys the bank)")
        {
            new Option<string>("-t") { Description = "access token for this run only — never persisted; defaults to BWS_ACCESS_TOKEN", HelpName = "token" }
        };
        encryption.Add(bitwarden);
        encryption.Add(new Command("show", "Shows the current encryption source"));
        encryption.Add(new Command("unset", "Returns to the env default (rekeys the bank when AIRACCOON_DB_PASSPHRASE is set)"));
        return encryption;
    }

    private static Command WatchCommand()
    {
        var watch = new Command("watch", "File-watcher configuration (CLI-only channel)")
        {
            new Command("enable", "Enables or disables watching for a target")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false" } },
            new Command("disable", "Alias for enable … false") { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false" } }
        };
        var scope = new Command("scope", "Scope allowlist (absolute paths, covers dir + subdirs)")
        {
            new Command("add", "Adds a scope path (normalized absolute, deduped, re-sorted)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("remove", "Removes a scope path") { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("list", "Lists a target's scope allowlist") { new Argument<string>("target") { HelpName = "project-id|*" } }
        };
        watch.Add(scope);
        watch.Add(new Command("concurrency", "Sets the watcher concurrency (1..16, default 4)")
            { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<int>("value") { HelpName = "1..16" } });
        watch.Add(new Command("list", "Lists every target's enabled flag, concurrency and scopes"));
        return watch;
    }
}
