namespace AiRaccoon;

public static class ExitCode
{
    /// <summary>No encryption key could be resolved: the key source (env, Bitwarden) is missing,
    /// unreachable or returned nothing usable.</summary>
    public const int FailedToResolveEncryptionKey = 1;

    /// <summary>The bank exists but could not be opened or read: the resolved key does not decrypt
    /// it, or SQLite refused (locked, busy, an I/O error).</summary>
    public const int FailedToOpenEncryptedBank = 2;
    public const int PortInUse = 3;
    public const int NoServerRunning = 4;
    public const int OtlpNotEnabled = 5;

    /// <summary>The proxy could neither reach nor start a backend; there is no in-process fallback (ADR-0020).</summary>
    public const int ProxyBackendUnavailable = 6;

    /// <summary>`serve` could not read, heal or mint a state-directory secret (the loopback token or the identity key); it refuses to bind unguarded.</summary>
    public const int McpTokenUnavailable = 7;

    // 8 was RestartFailed, one code for five different reasons. Split into 10-14 so a caller can
    // tell "retry me" from "fix your config"; 8 is retired rather than narrowed, so a script that
    // tested for it fails to match rather than matching the wrong case (ADR-0022).

    /// <summary>The argv does not fit the command grammar: an unknown or misplaced token, or a missing subcommand.</summary>
    public const int FailedToParseCliArgs = 9;

    /// <summary>`serve --restart`: another server took the port while this one was starting — retryable.</summary>
    public const int RestartLostThePort = 10;

    /// <summary>`serve --restart`: our data root holds no token, so the server cannot be asked to stop.</summary>
    public const int RestartNoToken = 11;

    /// <summary>`serve --restart`: the server refused our token — it serves another data root.</summary>
    public const int RestartTokenRefused = 12;

    /// <summary>`serve --restart`: the server is too old to be asked to stop.</summary>
    public const int RestartUnsupportedServer = 13;

    /// <summary>`serve --restart`: the server accepted the shutdown but still held the port at the bound.</summary>
    public const int RestartTimedOut = 14;

    /// <summary>
    ///     `serve --restart`: the port gave the probe no answer, so nothing was asked to stop, and the
    ///     bind then proved the port is held — retryable, unlike <see cref="PortInUse" /> (ADR-0043).
    /// </summary>
    public const int RestartProbeUnanswered = 16;

    /// <summary>
    ///     The argv fits the grammar but a value is missing or invalid (bad enum, out-of-range number,
    ///     missing required argument, removed option value), on a verb or a bare launch alike.
    /// </summary>
    public const int InvalidArgument = 15;

    /// <summary>A settings command (ADR-0075 §5.3) reached a server that refused the loopback token — it serves another data root.</summary>
    public const int SettingsServerRefused = 17;

    /// <summary>A settings command could neither reach nor auto-start a settings server within the acquire budget.</summary>
    public const int SettingsServerUnavailable = 18;

    /// <summary>`doctor` (GH #357): the bank's actual schema shape (tables/columns/indexes) differs from what this binary's DDL produces — distinct from the bank simply failing to open.</summary>
    public const int SchemaVerificationFailed = 19;

    /// <summary>`doctor`: the bank's stored user_version is newer than this binary's MemorySchema.CurrentVersion — a newer build wrote it, not a shape mismatch.</summary>
    public const int SchemaNewerThanBinary = 20;

    /// <summary>`model download` (plan D4/D8): a verified download failed — SHA mismatch, fetch
    /// failure, or the ORT opset smoke test rejected the graph. Distinct from InvalidArgument so
    /// a script can tell "you mistyped" from "the repo/network misbehaved".</summary>
    public const int ModelDownloadFailed = 21;

    /// <summary>`doctor` (delta review C3): no bank file exists at the resolved path — distinct
    /// from HEALTHY (0), so a wrong `--data-root` cannot read as a healthy bank. Also returned by a
    /// client auto-launch (F39: the proxy's private spawn, a settings verb's shared attach-or-start)
    /// refusing to mint a bank at a non-default root that does not have one yet.</summary>
    public const int NoBank = 22;

    /// <summary>A settings command (delta review C2) reached a server that answered but failed
    /// processing the request (5xx) — distinct from <see cref="InvalidArgument" /> so a script can
    /// tell "the server broke" from "you mistyped".</summary>
    public const int SettingsServerError = 23;

    /// <summary>`doctor`: the schema shape is healthy but a model_migration outbox row is open
    /// (ADR-0076) — every MCP tool call is refused until the re-embed finishes (ADR-0087), so the
    /// bank is not healthy even though its shape is. Same species as <see cref="SchemaNewerThanBinary" />:
    /// legitimate, transient, self-clearing. Reachable only from the Healthy arm — 19/20 outrank it
    /// (review R1 Ruling 4).</summary>
    public const int ModelMigrationOpen = 24;

    /// <summary>A model verb (`settings model reset`, `model embedding set`) refused by the settings
    /// server because a model_migration outbox row is open (ADR-0076); nothing changed. Same species
    /// as <see cref="ModelMigrationOpen" /> (24), but a settings-verb refusal.</summary>
    public const int ModelResetRefused = 25;

    /// <summary>`doctor` (F7, ruling K7): the bank file exists but is not a SQLite database — corrupt,
    /// or not readable with the resolved encryption key. Distinct from <see cref="NoBank" /> (no file
    /// at all) and <see cref="FailedToOpenEncryptedBank" /> (a real database whose open failed).</summary>
    public const int BankCorrupted = 26;

    /// <summary>The command failed for a reason no other code names — an I/O fault, a server
    /// response it could not use, or an unexpected error; stderr says which. Never a bad argument.</summary>
    public const int CommandFailed = 27;

    /// <summary>The command was cancelled before it finished (Ctrl-C / SIGTERM — 130 is 128 +
    /// SIGINT(2), the shell's conventional interrupt code); the command changed nothing.</summary>
    public const int Interrupted = 130;

    public const int Success = 0;
}
