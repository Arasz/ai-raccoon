using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup;

public static class DefaultOptions
{
    public const int Port = 7721;

    /// <summary>ADR-0020: bare launches proxy to one HTTP backend; --transport stdio is the escape hatch.</summary>
    public const McpTransport Transport = McpTransport.Proxy;

    public const InstallScope InstallScope = Infrastructure.Options.InstallScope.User;
    public static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon");
    internal static TimeSpan IdleTimeout => TimeSpan.FromHours(4);
}
