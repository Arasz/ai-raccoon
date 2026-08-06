using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup;

public static class DefaultOptions
{
    public const int Port = 7721;
    public const McpTransport Transport = McpTransport.Stdio;
    public const InstallScope InstallScope = Infrastructure.Options.InstallScope.User;
    public static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon");
}
