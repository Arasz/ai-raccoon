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

    public const int Success = 0;
}
