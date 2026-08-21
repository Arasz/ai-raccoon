namespace AiRaccoon;

public static class ExitCode
{
    public const int FailedToResolveEncryptionKey = 1;
    public const int FailedToOpenEncryptedBank = 2;
    public const int PortInUse = 3;
    public const int NoServerRunning = 4;
    public const int OtlpNotEnabled = 5;

    /// <summary>The proxy could neither reach nor start a backend; there is no in-process fallback (ADR-0020).</summary>
    public const int ProxyBackendUnavailable = 6;

    /// <summary>`serve` could not read, heal or mint the loopback token; it refuses to bind unguarded.</summary>
    public const int McpTokenUnavailable = 7;

    // 8 was RestartFailed, one code for five different reasons. Split into 10-14 so a caller can
    // tell "retry me" from "fix your config"; 8 is retired rather than narrowed, so a script that
    // tested for it fails to match rather than matching the wrong case (ADR-0022).

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

    /// <summary>A CLI verb's own argument failed validation (bad enum value, out-of-range number, missing prompt input, etc.) — distinct from the specific failure codes above so a script can tell "you mistyped" from "the bank/server is broken". Was 10 until #286 claimed 10-14 for restart reasons.</summary>
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

    public const int Success = 0;
}
