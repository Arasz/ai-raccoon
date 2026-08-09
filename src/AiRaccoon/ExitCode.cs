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

    public const int Success = 0;
}
