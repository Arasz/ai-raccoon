using System.CommandLine;
using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup.Cli;

/// <summary>
///     Defines the CLI surface: the verb families, the launch roots, and the options they
///     accept. The only growth point for a new verb family (a builder + one root.Add +
///     one Verbs entry).
/// </summary>
internal static class CliCommandTree
{
    private const string Description = "MCP server exposing agent memory over sqlite-memory";

    internal static readonly string[] Verbs = ["access", "model", "retrieval", "sweep", "sync", "watch", "encryption", "extract"];

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
        root.Add(ExtractCommand());
        return root;
    }

    /// <summary>Launch-only root for bare server invocations (no verb): System.CommandLine
    /// treats a root with subcommands as requiring one, so verb-less flag sets re-parse here.</summary>
    internal static RootCommand BuildLaunchRootCommand()
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
        root.Add(new Option<int>("--port") { Description = "HTTP port to bind; 0 picks a random free port", HelpName = "port", DefaultValueFactory = _ => 7721 });
        root.Add(new Option<bool>("--status-words") { Description = "One-word status on stderr per tool call; info logs quieted" });
        // WebApplicationFactory bootstraps the entry point with these host-config flags;
        // declared hidden so the E2E host builds — values are intentionally never consumed
        // (CreateBuilder([]) drops generic host flags by design).
        root.Add(new Option<string>("--environment") { Hidden = true });
        root.Add(new Option<string>("--contentRoot") { Hidden = true });
        root.Add(new Option<string>("--applicationName") { Hidden = true });
    }

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
        var watch = new Command("watch",
            "Watch configuration (CLI-only channel): enable/disable, scope allowlist and concurrency per target. This family CONFIGURES watching — it does not register watches; registrations are created by agents via the memory_watch_add MCP tool.")
        {
            new Command("enable", "Enables or disables watching for a target (configuration only — does not register a watch; use memory_watch_add to register)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false" } },
            new Command("disable", "Alias for enable … false") { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false" } }
        };
        var scope = new Command("scope", "Scope allowlist (absolute paths, covers dir + subdirs) — the paths a registered watch must sit under")
        {
            new Command("add", "Adds a scope path (normalized absolute, deduped, re-sorted)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("remove", "Removes a scope path") { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("list", "Lists a target's scope allowlist") { new Argument<string>("target") { HelpName = "project-id|*" } }
        };
        watch.Add(scope);
        watch.Add(new Command("concurrency", "Sets the watcher concurrency (1..16, default 4)")
            { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<int>("value") { HelpName = "1..16" } });
        watch.Add(new Command("list", "Lists each target's watch CONFIGURATION (enabled, concurrency, scope) — not registered watches; use 'watch registered' for those"));
        watch.Add(new Command("registered",
                "Lists every REGISTERED watch (project, path, registered at, last change) from the watches table. Registrations are created via memory_watch_add; live state (scanning/healthy/…) is reported by memory_watch_status, not the CLI.")
            { new Argument<string?>("project-id") { HelpName = "project-id", Arity = ArgumentArity.ZeroOrOne } });
        watch.Add(new Command("remove", "Removes all watch config rows for a target") { new Argument<string>("target") { HelpName = "project-id|*" } });
        return watch;
    }

    private static Command ExtractCommand()
    {
        var extract = new Command("extract",
            "Background shared-extraction configuration (CLI-only channel): enables or disables the hosted service that periodically checks each project's committed memories and extracts the shared-worthy ones, and sets its mode (propose logs candidates; promote shares them).")
        {
            new Command("enable", "Enables or disables the background shared-extraction service (disabled by default; promote mode shares data between projects)")
                { new Argument<bool>("enabled") { HelpName = "true|false" } },
            new Command("mode", "Sets the extraction mode: propose (default, logs ranked candidates) or promote (shares the top candidates into the shared tier)")
                { new Argument<string>("mode") { HelpName = "propose|promote" } },
            new Command("interval", "Sets the extraction pass interval in minutes (positive integer; default 60)")
                { new Argument<string>("minutes") { HelpName = "minutes" } },
            new Command("list", "Shows the extraction configuration (enabled, mode, interval minutes)")
        };
        return extract;
    }
}
