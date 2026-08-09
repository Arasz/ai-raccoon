namespace AiRaccoon;

public static class ExitCode
{
    public const int FailedToResolveEncryptionKey = 1;
    public const int FailedToOpenEncryptedBank = 2;
    public const int PortInUse = 3;
    public const int NoServerRunning = 4;
    public const int OtlpNotEnabled = 5;
    // 6 is reserved for the proxy's ProxyBackendUnavailable (ADR-0020), which lands on its own branch.
    public const int McpTokenUnavailable = 7;
    public const int Success = 0;
}
