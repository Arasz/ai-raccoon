using System.CommandLine;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup.Cli;

/// <summary>
///     Defines the CLI surface: the verb families, the launch roots, and the options they
///     accept. A new settings-backed subsystem is a node under `settings` (ADR-0076), not a new
///     top-level family.
/// </summary>
internal static class CliCommandTree
{
    private const string Description = "MCP server exposing agent memory over sqlite-memory";

    /// <summary>Derived from McpTransport so a new transport cannot leave the help name stale.</summary>
    private static readonly string TransportHelpName =
        string.Join('|', Enum.GetNames<McpTransport>().Select(name => name.ToLowerInvariant()));

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
        root.Add(SettingsCommand());
        root.Add(ModelCommand());
        root.Add(WatchCommand());
        root.Add(ExtractCommand());
        root.Add(NoiseCommand());
        root.Add(EncryptionCommand());
        root.Add(ServeCommand());
        root.Add(DoctorCommand());
        return root;
    }

    /// <summary>
    ///     Every subsystem's configuration, one node per subsystem (ADR-0076): a new settings-backed
    ///     subsystem is a node here, not a new top-level family nobody remembers to add.
    /// </summary>
    private static Command SettingsCommand() =>
        new("settings", "Runtime configuration, one node per subsystem. Operations live at the top level: 'watch registered', 'extract prune', 'noise entries', 'model set', 'encryption', 'serve'.")
        {
            AccessCommand(),
            SettingsModelCommand(),
            RetrievalCommand(),
            SweepCommand(),
            SettingsNoiseCommand(),
            QueryGuardCommand(),
            SyncCommand(),
            IngestCommand(),
            SettingsWatchCommand(),
            SettingsExtractCommand(),
            MaintenanceCommand(),
            PerformanceCommand()
        };

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

    /// <summary>
    ///     `model set` re-embeds the whole bank (ADR-0076: the CLI commits an outbox record and
    ///     returns; a relay on the server drains it), so it is an operation and stays top level;
    ///     the provider rows it leaves behind are read and cleared under settings. The help says
    ///     "blocks", not "in the background": the command returns immediately, but the bank refuses
    ///     every tool call until the re-embed finishes, and "background" told users the opposite.
    /// </summary>
    private static Command ModelCommand() =>
        new("model", "Embedding engine selection; configuration is under 'settings model'")
        {
            new Command("set",
                "Sets the engine and re-embeds the bank. Blocks all reads and writes until done — minutes on a large bank")
            {
                new Command("local", "Embeds in-process with the bundled ONNX model; optional path overrides it") { new Argument<string?>("path") { HelpName = "path", Arity = ArgumentArity.ZeroOrOne } },
                new Command("openai", "Routes through an OpenAI-compatible endpoint; key via --api-key (persisted in settings)")
                {
                    new Argument<string>("model") { HelpName = "model-id" }, new Argument<string?>("base-url") { HelpName = "url", Arity = ArgumentArity.ZeroOrOne },
                    new Option<string>("--api-key") { Description = "API key persisted in the settings table", HelpName = "key" }
                }
            }
        };

    private static Command SettingsModelCommand()
    {
        var model = new Command("model", "Embedding engine configuration (the engine itself is selected by 'model set')");
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
        var fusion = new Command("fusion",
            "No-fusion-regression reorder (docs/adr/0078): keeps a result from ranking below where its best single modality put it. OFF by default and unproven — 'fusion enable' arms it and starts recording how it differs from the baseline.")
        {
            new Command("enable", "Arms the reorder and its evidence collection (not the default)"),
            new Command("disable", "Back to the baseline fusion (the default)")
        };
        var fusionShow = new Command("show", "Shows whether the reorder is armed, and names the default");
        fusionShow.Aliases.Add("list");
        fusion.Add(fusionShow);
        retrieval.Add(fusion);
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

    private static Command SettingsNoiseCommand()
    {
        var noise = new Command("noise",
            "Pre-write noise rejection: the kill switch for the deterministic Hermes background-process-log filter. Rejection is ON by default — 'noise disable' is how you disarm it. Entries the filter rejected are read via 'noise entries', not here — that reads the noise_entries data table, not settings.")
        {
            new Command("enable", "Arms pre-write noise rejection (the default)"),
            new Command("disable", "Disarms pre-write noise rejection — every write is stored, even ones a policy would otherwise refuse")
        };
        var show = new Command("show", "Shows whether pre-write noise rejection is enabled");
        show.Aliases.Add("list");
        noise.Add(show);
        return noise;
    }

    /// <summary>The noise_entries table is data, not configuration, so summarizing it stays top level.</summary>
    private static Command NoiseCommand() =>
        new("noise", "Rejected-write training data. Rejection is CONFIGURED under 'settings noise'.")
        {
            new Command("entries", "Summarizes noise_entries — the training-data source for a future noise learner (ADR-0029/ADR-0039)")
        };

    private static Command QueryGuardCommand()
    {
        var queryGuard = new Command("queryguard",
            "Read-path query guard (docs/adr/0040): refuses a memory_search query that is itself machine output (e.g. a pasted background-process notification) and annotates one that merely contains log-like content. Armed by default — 'queryguard disable' is how you disarm it.")
        {
            new Command("enable", "Arms the read-path query guard (the default)"),
            new Command("disable", "Disarms the read-path query guard — every query runs untouched, even ones a policy would otherwise refuse or annotate")
        };
        var shadow = new Command("shadow",
            "Shadow mode: records what the guard would have done without refusing or annotating anything — measure real traffic before enabling")
        {
            new Command("enable", "Arms shadow mode (off by default)"),
            new Command("disable", "Disarms shadow mode (the default): the guard acts on its verdicts")
        };
        queryGuard.Add(shadow);
        var structural = new Command("structural",
            "Structural detector (docs/adr/0041): a learned, vocabulary-free third input to the warn tier. Off by default — it only ever adds an annotation, never a refusal.")
        {
            new Command("enable", "Arms the structural detector"),
            new Command("disable", "Disarms the structural detector (the default)"),
            new Command("threshold", "Score a query must clear before the detector annotates it")
            {
                new Command("set", "Sets queryGuard.structural.threshold (0..1)")
                    { new Argument<string>("threshold") { HelpName = "0..1" } }
            }
        };
        queryGuard.Add(structural);
        var show = new Command("show", "Shows whether the guard, shadow mode and the structural detector are enabled");
        show.Aliases.Add("list");
        queryGuard.Add(show);
        return queryGuard;
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

    /// <summary>
    ///     Verifies the bank's schema shape (tables/columns/indexes) against what this binary's DDL
    ///     produces and reports — it never repairs (GH #357). An operation, not settings-backed
    ///     configuration, so it stays top level like `watch`/`extract`/`noise`/`encryption`/`serve`.
    /// </summary>
    private static Command DoctorCommand() =>
        new("doctor",
            "Verifies the bank's schema shape (tables, columns, indexes) against what this binary expects, and reports. Never repairs — run after a suspected aborted migration, hand-edited bank, or partial restore.");

    private static Command ScopeCommand(string description) =>
        new("scope", description)
        {
            new Command("add", "Adds a scope path (normalized absolute, deduped, re-sorted)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("remove", "Removes a scope path")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<string>("path") { HelpName = "path" } },
            new Command("list", "Lists a target's scope allowlist") { new Argument<string>("target") { HelpName = "project-id|*" } }
        };

    private static Command IngestCommand() =>
        new("ingest",
            "Ingestion configuration. The scope allowlist bounds every path the server reads from disk — memory_ingest_file, memory_ingest_directory, memory_watch_add and the file watcher. It is empty by default, so a project ingests nothing until a scope is added.")
        {
            ScopeCommand("Scope allowlist (absolute paths, covers dir + subdirs) — the paths this server may read")
        };

    /// <summary>The watches table is data, not configuration, so listing registrations stays top level.</summary>
    private static Command WatchCommand() =>
        new("watch", "Registered watches. Watching is CONFIGURED under 'settings watch'; registrations are created by agents via the memory_watch_add MCP tool.")
        {
            new Command("registered",
                    "Lists every REGISTERED watch (project, path, registered at, last change) from the watches table. Registrations are created via memory_watch_add; live state (scanning/healthy/…) is reported by memory_watch_status, not the CLI.")
                { new Argument<string?>("project-id") { HelpName = "project-id", Arity = ArgumentArity.ZeroOrOne } }
        };

    private static Command SettingsWatchCommand()
    {
        var watch = new Command("watch",
            "Watch configuration: enable/disable and concurrency per target. This node CONFIGURES watching — it does not register watches; use 'watch registered' to list registrations.")
        {
            new Command("enable", "Enables or disables watching for a target (configuration only — does not register a watch; use memory_watch_add to register)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false", Arity = ArgumentArity.ExactlyOne } },
            new Command("disable", "Alias for enable … false") { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<bool>("enabled") { HelpName = "true|false", Arity = ArgumentArity.ExactlyOne } },
            new Command("concurrency", "Sets the watcher concurrency (1..16, default 4)")
                { new Argument<string>("target") { HelpName = "project-id|*" }, new Argument<int>("value") { HelpName = "1..16" } },
            new Command("remove", "Removes all watch config rows for a target") { new Argument<string>("target") { HelpName = "project-id|*" } }
        };
        var list = new Command("list", "Lists each target's watch CONFIGURATION (enabled, concurrency, scope) — not registered watches; use 'watch registered' for those");
        list.Aliases.Add("show");
        watch.Add(list);
        return watch;
    }

    /// <summary>`extract prune` deletes promotion_queue rows (ADR-0023), so it is an operation.</summary>
    private static Command ExtractCommand() =>
        new("extract", "Shared-extraction operations. The service is CONFIGURED under 'settings extract'.")
        {
            new Command("prune",
                    "Reports promotion_queue rows orphaned before the entries-delete trigger existed (ADR-0023) — a candidate whose backing entry is gone. Reports per-project counts by default; --apply removes them. Idempotent.")
                { new Option<bool>("--apply") { Description = "Removes the orphaned rows instead of only reporting them" } }
        };

    private static Command SettingsExtractCommand()
    {
        var extract = new Command("extract",
            "Background shared-extraction configuration: enables or disables the hosted service that periodically checks each project's committed memories and extracts the shared-worthy ones, and sets its mode (propose logs candidates; promote shares them).")
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
            }
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

    private static Command PerformanceCommand()
    {
        var performance = new Command("performance",
            "Metrics subsystem configuration (CLI-only channel): buffer capacity and flush interval tune the background writer, hot-table retention tunes the reaper. Each takes effect on a different cadence — buffer capacity on the next server restart, flush interval on the next flush tick, retention on the next maintenance pass — every command below states its own.")
        {
            new Command("buffer-capacity",
                    $"Sets the measurement buffer capacity (positive integer, max {MetricsConfigKeys.MaxBufferCapacity}; default {MetricsConfigKeys.DefaultBufferCapacity}) — takes effect on the next server restart")
                { new Argument<string>("capacity") { HelpName = "capacity" } },
            new Command("flush-interval",
                    $"Sets the metrics flush interval in seconds (positive integer; default {MetricsConfigKeys.DefaultFlushIntervalSeconds}) — takes effect on the next flush tick")
                { new Argument<string>("seconds") { HelpName = "seconds" } },
            new Command("retention",
                    $"Sets the hot metrics-table retention in days (positive integer, max {MetricsConfigKeys.MaxRetentionDays}; default {MetricsConfigKeys.DefaultRetentionDays}) — takes effect on the next maintenance pass")
                { new Argument<string>("days") { HelpName = "days" } }
        };
        var list = new Command("list", "Shows the metrics subsystem configuration (buffer capacity, flush interval, retention)");
        list.Aliases.Add("show");
        performance.Add(list);
        return performance;
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
