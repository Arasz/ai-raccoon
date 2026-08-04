using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup;

/// <summary>
///     Launch identity: transport + bank options. Built by <see cref="Build"/> from the
///     launch flags (CLI only — env handling was removed by the single-channel ruling;
///     runtime configuration lives in the settings table via the config commands).
/// </summary>
internal sealed record ServerConfig(McpTransport Transport, InfrastructureOptions Options)
{
    internal static ServerConfig Build(CliOptions? cli)
    {
        var transport = ParseTransport(cli?.Transport);
        var dataRoot = ExpandTilde(NonBlank(cli?.DataRoot));

        var options = new InfrastructureOptions
        {
            DataRoot = dataRoot ?? InfrastructureOptions.DefaultDataRoot(),
            Scope = cli?.InstallScope ?? InstallScope.User
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
