using System.CommandLine;
using AiRaccoon.Hosting.Node;
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

    /// <summary>Derived from McpTransport so a new transport cannot leave the help name stale.</summary>
    private static readonly string TransportHelpName =
        string.Join('|', Enum.GetNames<McpTransport>().Select(name => name.ToLowerInvariant()));

    internal static readonly string[] Verbs = ["access", "model", "retrieval", "sweep", "noise", "sync", "ingest", "watch", "encryption", "extract", "maintenance", "serve"];

    /// <summary>
    ///     The root launch --port (shared with the bare launch root); serve reads it instance-based
    ///     as its fallback when serve's own --port is absent (docs/plans/2026-08-06-http-serve-mode-plan.md R7/R12).
    /// </summary>
    internal static readonly Option<int> LaunchPortOption = new("--port")
    {
        Description = "HTTP backend port the proxy dials or starts (1-65535); 0 is serve-only",
        HelpName = "port",
        DefaultValueFactory = _ => 7721
    };

    internal static readonly Option<int> ServePortOption = new("--port")
    {
        Description = "HTTP port to bind; 0 picks a random free port",
        HelpName = "port",
        DefaultValueFactory = _ => 7721
    };

    internal static readonly Option<string> ServeIdleTimeoutOption = CreateIdleTimeoutOption();

    internal static readonly Option<bool> ServeMcpEntryOption = new("--mcp-entry")
    {
        Description = "Print the MCP client config entry for the bound URL"
    };

    /// <summary>Cycles the server already on the port instead of attaching to it (ADR-0022).</summary>
    internal static readonly Option<bool> ServeRestartOption = new("--restart")
    {
        Description = "Stop the ai-raccoon server already on the port and serve in its place (a plain start when none is)"
    };

    internal static readonly Option<string> ServeFormatOption = CreateFormatOption();

    /// <summary>
    ///     observability's own --port, read instance-based like ServePortOption
    ///     (docs/plans/2026-08-06-http-serve-mode-plan.md R12): unlike serve's --port, 0 is not
    ///     legal here — there is no "any free port" to dial.
    /// </summary>
    internal static readonly Option<int> ObservabilityPortOption = CreateObservabilityPortOption();

    /// <summary>The full tree: launch flags + verb commands (help rendered from this root shows the verbs).</summary>
    internal static RootCommand BuildFullRootCommand()
    {
        var root = new RootCommand(Description);
        AddLaunchOptions(root);
        root.Add(AccessCommand());
        root.Add(ModelCommand());
        root.Add(RetrievalCommand());
        root.Add(SweepCommand());
        root.Add(NoiseCommand());
        root.Add(SyncCommand());
        root.Add(IngestCommand());
        root.Add(WatchCommand());
        root.Add(EncryptionCommand());
        root.Add(ExtractCommand());
        root.Add(MaintenanceCommand());
        root.Add(ServeCommand());
        return root;
    }

    /// <summary>
    ///     Launch-only root for bare server invocations (no verb): System.CommandLine
    ///     treats a root with subcommands as requiring one, so verb-less flag sets re-parse here.
    /// </summary>
    internal static RootCommand BuildLaunchRootCommand()
    {
        var root = new RootCommand(Description);
        AddLaunchOptions(root);
        return root;
    }

    private static void AddLaunchOptions(RootCommand root)
    {
        root.Add(new Option<McpTransport>("--transport")
        {
            Description = "MCP transport; proxy (default) relays to one HTTP backend, https unsupported",
            HelpName = TransportHelpName
        });
        root.Add(new Option<string>("--data-root") { Description = "Bank data root (must precede the verb)", HelpName = "path" });
        root.Add(new Option<InstallScope>("--install-scope") { Description = "Install scope (must precede the verb)", HelpName = "user|project" });
        root.Add(new Option<bool>("--quiet") { Description = "Quiet mode: every log level goes to a file beside the bank, nothing reaches stdout/stderr" });
        root.Add(LaunchPortOption);
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
        var accessList = new Command("list", "Lists the default and every override");
        accessList.Aliases.Add("show");
        access.Add(accessList);
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
        var reset = new Command("reset", "Back to default: no engine (FTS5-only search)");
        reset.Aliases.Add("unset");
        reset.Aliases.Add("remove");
        model.Add(reset);
        var show = new Command("show", "Shows the configured provider/model/baseUrl/engine");
        show.Aliases.Add("list");
        model.Add(show);
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
        var sweep = new Command("sweep",
            "Background reaper configuration: the kill switch, the cadence and the rating threshold it deletes below. The reaper is ON by default — 'sweep disable' is how you disarm it. Per-entry TTLs are data, set by the memory_set_ttl tool, not configured here.")
        {
            new Command("enable", "Arms the background reaper (the default: it deletes expired entries on its cadence)"),
            new Command("disable", "Disarms the background reaper — nothing is deleted until it is enabled again"),
            new Command("interval-hours", "Sets the reaper cadence in hours (1..8760, default 24); applies live, no server restart needed")
                { new Argument<string>("hours") { HelpName = "1..8760" } }
        };
        var threshold = new Command("threshold", "Sweep rating threshold")
        {
            new Command("set", "Sets sweep.threshold (0..1, default 0.3)") { new Argument<string>("threshold") { HelpName = "0..1" } }
        };
        sweep.Add(threshold);
        var show = new Command("show", "Shows the whole policy: enabled, interval hours and threshold (row values, else the defaults)");
        show.Aliases.Add("list");
        sweep.Add(show);
        return sweep;
    }

    private static Command NoiseCommand()
    {
        var noise = new Command("noise",
            "Pre-write noise rejection: the kill switch for the deterministic Hermes background-process-log filter. Rejection is ON by default — 'noise disable' is how you disarm it.")
        {
            new Command("enable", "Arms pre-write noise rejection (the default)"),
            new Command("disable", "Disarms pre-write noise rejection — every write is stored, even ones a policy would otherwise refuse")
        };
        var show = new Command("show", "Shows whether pre-write noise rejection is enabled");
        show.Aliases.Add("list");
        noise.Add(show);
        return noise;
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
        var remove = new Command("remove", "Back to default: sync off");
        remove.Aliases.Add("reset");
        remove.Aliases.Add("unset");
        sync.Add(remove);
        var show = new Command("show", "Shows the sync configuration (keys redacted)");
        show.Aliases.Add("list");
        sync.Add(show);
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
        var show = new Command("show", "Shows the current encryption source");
        show.Aliases.Add("list");
        encryption.Add(show);
        var unset = new Command("unset", "Returns to the env default (rekeys the bank when AIRACCOON_DB_PASSPHRASE is set)");
        unset.Aliases.Add("reset");
        unset.Aliases.Add("remove");
        encryption.Add(unset);
        encryption.Add(new Command("migrate", "Rekeys a bank still encrypted under the pre-ADR-0012 key derivation (ADR-0012)"));
        return encryption;
    }

    /// <summary>The scope allowlist subtree, mounted under both `ingest` (canonical) and `watch` (alias).</summary>
    private static Command ScopeCommand(string description) =>
        new("scope", description)
        {
            new Command("add", "Adds a scope path (normalized absolute, deduped, re-sorted)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("remove", "Removes a scope path")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("list", "Lists a target's scope allowlist") { new Argument<string>("target") { HelpName = "project-id|*" } }
        };

    private static Command IngestCommand()
    {
        var ingest = new Command("ingest",
                "Ingestion configuration (CLI-only channel). The scope allowlist bounds every path the server reads from disk — memory_ingest_file, memory_ingest_directory, memory_watch_add and the file watcher. It is empty by default, so a project ingests nothing until a scope is added.")
            { ScopeCommand("Scope allowlist (absolute paths, covers dir + subdirs) — the paths this server may read") };
        return ingest;
    }

    private static Command WatchCommand()
    {
        var watch = new Command("watch",
            "Watch configuration (CLI-only channel): enable/disable, scope allowlist and concurrency per target. This family CONFIGURES watching — it does not register watches; registrations are created by agents via the memory_watch_add MCP tool.")
        {
            new Command("enable", "Enables or disables watching for a target (configuration only — does not register a watch; use memory_watch_add to register)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false", Arity = ArgumentArity.ExactlyOne } },
            new Command("disable", "Alias for enable … false") { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false", Arity = ArgumentArity.ExactlyOne } },
            // Kept as a deprecated alias: the scope moved to `ingest scope` when it stopped being
            // watch-only, and breaking every existing setup script at the same time is gratuitous.
            ScopeCommand("Deprecated alias for 'ingest scope' — the allowlist bounds all ingestion, not just watching"),
            new Command("concurrency", "Sets the watcher concurrency (1..16, default 4)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<int>("value") { HelpName = "1..16" } },
            new Command("registered",
                    "Lists every REGISTERED watch (project, path, registered at, last change) from the watches table. Registrations are created via memory_watch_add; live state (scanning/healthy/…) is reported by memory_watch_status, not the CLI.")
                { new Argument<string?>("project-id") { HelpName = "project-id", Arity = ArgumentArity.ZeroOrOne } },
            new Command("remove", "Removes all watch config rows for a target") { new Argument<string>("target") { HelpName = "project-id|*" } }
        };
        var list = new Command("list", "Lists each target's watch CONFIGURATION (enabled, concurrency, scope) — not registered watches; use 'watch registered' for those");
        list.Aliases.Add("show");
        watch.Add(list);
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
            new Command("interval", "Sets the extraction pass interval in minutes (positive integer; default 30)")
                { new Argument<string>("minutes") { HelpName = "minutes" } },
            new Command("capacity", "Sets the propose-tier queue capacity (positive integer; default 1000) — the total queued candidates across projects, split into per-project reservations")
                { new Argument<string>("capacity") { HelpName = "capacity" } },
            new Command("exclude", "Excludes source_file prefixes from shared-extraction candidacy (extract.exclude.prefixes; e.g. 'scratch/' keeps agent scratch files out of the shared tier)")
            {
                new Command("add", "Adds a source_file prefix to the exclusion list (deduped)")
                    { new Argument<string>("prefix") { HelpName = "prefix" } },
                new Command("remove", "Removes a source_file prefix from the exclusion list")
                    { new Argument<string>("prefix") { HelpName = "prefix" } },
                new Command("list", "Lists the excluded source_file prefixes")
            },
            new Command("prune",
                    "Reports promotion_queue rows orphaned before the entries-delete trigger existed (ADR-0023) — a candidate whose backing entry is gone. Reports per-project counts by default; --apply removes them. Idempotent.")
                { new Option<bool>("--apply") { Description = "Removes the orphaned rows instead of only reporting them" } }
        };
        var list = new Command("list", "Shows the extraction configuration (enabled, mode, interval minutes)");
        list.Aliases.Add("show");
        extract.Add(list);
        return extract;
    }

    private static Option<string> CreateFormatOption()
    {
        var option = new Option<string>("--format")
        {
            Description = "Entry format: hermes|claude|all",
            HelpName = "format",
            DefaultValueFactory = _ => "hermes"
        };
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (value is not ("hermes" or "claude" or "all"))
            {
                result.AddError($"Cannot parse argument '{value}' as an entry format: expected hermes|claude|all.");
            }
        });
        return option;
    }

    private static Option<string> CreateIdleTimeoutOption()
    {
        var option = new Option<string>("--idle-timeout")
        {
            Description = "Idle shutdown span: 90s/30m/4h/1d; 0 disables",
            HelpName = "span"
        };
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (!IdleTimeoutParser.TryParse(value, out _))
            {
                result.AddError($"Cannot parse argument '{value}' as an idle timeout: expected 90s/30m/4h/1d or 0 (disabled).");
            }
        });
        return option;
    }

    private static Command MaintenanceCommand()
    {
        var maintenance = new Command("maintenance",
            "Bank maintenance configuration (CLI-only channel): the checkpoint interval bounds the WAL — every process runs wal_checkpoint(TRUNCATE) at startup and shutdown, and the periodic timer on this cadence — and the vacuum interval sets how often VACUUM + ANALYZE run (per-process clock; short-lived processes never vacuum).")
        {
            new Command("interval", "Sets the WAL checkpoint interval in minutes (positive integer; default 60)")
                { new Argument<string>("minutes") { HelpName = "minutes" } },
            new Command("vacuum-interval", "Sets the VACUUM + ANALYZE interval in days (positive integer; default 7)")
                { new Argument<string>("days") { HelpName = "days" } }
        };
        var list = new Command("list", "Shows the bank maintenance configuration (checkpoint interval, vacuum interval)");
        list.Aliases.Add("show");
        maintenance.Add(list);
        return maintenance;
    }

    private static Command ServeCommand()
    {
        var serve = new Command("serve",
            "Serves the MCP endpoint over HTTP (always HTTP). Background it: ai-raccoon serve > serve.log 2>&1 &")
        {
            ServePortOption,
            ServeIdleTimeoutOption,
            ServeMcpEntryOption,
            ServeFormatOption,
            ServeRestartOption,
            ObservabilityCommand()
        };
        // SetAction exists only because System.CommandLine requires a subcommand unless the
        // command declares its own action; Program.cs actually routes on CommandPath. Do not remove.
        serve.SetAction(_ => ExitCode.Success);
        return serve;
    }

    private static Command ObservabilityCommand()
    {
        var kind = new Argument<string>("kind") { HelpName = "counters|trace|otlp|pid" };
        kind.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (value is not ("counters" or "trace" or "otlp" or "pid"))
            {
                result.AddError($"Cannot parse argument '{value}' as an observability kind: expected counters|trace|otlp|pid.");
            }
        });

        return new Command("observability", "Prints a ready-to-run monitoring command for the live server, with its PID filled in")
        {
            kind,
            ObservabilityPortOption
        };
    }

    private static Option<int> CreateObservabilityPortOption()
    {
        var option = new Option<int>("--port")
        {
            Description = "Port of the running serve process to query",
            HelpName = "port",
            DefaultValueFactory = _ => 7721
        };
        option.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int>();
            switch (value)
            {
                case 0:
                    result.AddError("Cannot parse argument '0' as --port: 0 means \"any free port\" and cannot be dialled; pass the port of the running serve process.");
                    break;
                case < 1 or > 65535:
                    result.AddError($"Cannot parse argument '{value}' as --port: expected 1-65535.");
                    break;
            }
        });
        return option;
    }
}
