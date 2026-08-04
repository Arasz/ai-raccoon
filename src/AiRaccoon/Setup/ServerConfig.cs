using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup;

/// <summary>
///     The merged runtime configuration: transport + fully-resolved infrastructure options.
///     Built by <see cref="Build"/> applying CLI args &gt; env vars &gt; built-in defaults per
///     key; secret env vars are never read here — they stay environment-only by design.
/// </summary>
internal sealed record ServerConfig(McpTransport Transport, InfrastructureOptions Options)
{
    internal static ServerConfig Build(CliOptions? cli, Func<string, string?> readEnv)
    {
        var transport = ParseTransport(cli?.Transport ?? readEnv("MCP_TRANSPORT"));
        var dataRoot = ExpandTilde(NonBlank(cli?.DataRoot) ?? NonBlank(readEnv("AIRACCOON_DATA_ROOT")));
        var scope = cli?.InstallScope
            ?? (string.Equals(readEnv("AIRACCOON_INSTALL_SCOPE"), "project", StringComparison.OrdinalIgnoreCase)
                ? InstallScope.Project
                : InstallScope.User);
        var embeddingModel = ExpandTilde(NonBlank(cli?.EmbeddingModel) ?? NonBlank(readEnv("AIRACCOON_EMBEDDING_MODEL")));

        var options = new InfrastructureOptions
        {
            DataRoot = dataRoot ?? InfrastructureOptions.DefaultDataRoot(),
            Scope = scope,
            AccessMode = cli?.AccessMode ?? readEnv("AIRACCOON_ACCESS_MODE"),
            EmbeddingModelPath = embeddingModel,
            Sync = new SyncOptions
            {
                Endpoint = cli?.SyncEndpoint ?? readEnv("AIRACCOON_SYNC_ENDPOINT"),
                Bucket = cli?.SyncBucket ?? readEnv("AIRACCOON_SYNC_BUCKET"),
                Region = cli?.SyncRegion ?? readEnv("AIRACCOON_SYNC_REGION"),
                ObjectKey = cli?.SyncObjectKey ?? readEnv("AIRACCOON_SYNC_OBJECT_KEY")
            }
        };

        return new ServerConfig(transport, options);
    }

    private static McpTransport ParseTransport(string? value) =>
        Enum.TryParse<McpTransport>(value, true, out var transport) ? transport : McpTransport.Stdio;

    /// <summary>Null or whitespace counts as unset (preserves the IsNullOrWhiteSpace gates).</summary>
    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ExpandTilde(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
    }
}
