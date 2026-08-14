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

    /// <summary>`serve --restart` could not cycle the server on the port; it never attaches instead (ADR-0022).</summary>
    public const int RestartFailed = 8;

    public const int FailedToParseCliArgs = 9;

    /// <summary>A CLI verb's own argument failed validation (bad enum value, out-of-range number, missing prompt input, etc.) — distinct from the specific failure codes above so a script can tell "you mistyped" from "the bank/server is broken".</summary>
    public const int InvalidArgument = 10;

    public const int Success = 0;
}
