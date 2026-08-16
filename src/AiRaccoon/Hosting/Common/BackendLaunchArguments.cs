using System.Globalization;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     The launch identity a caller hands <c>BackendLauncher.AcquireAsync</c> to auto-start
///     <c>ai-raccoon serve</c> on <see cref="ServerConfig.Port" />: the flags every backend needs
///     regardless of who is acquiring it (the stdio proxy, or a CLI settings command, ADR-0075
///     §5.1) precede the verb.
/// </summary>
internal static class BackendLaunchArguments
{
    /// <summary>This very binary: the backend is another ai-raccoon, started as `serve`.</summary>
    public static string? Executable() => Environment.ProcessPath;

    public static string[] ServeArguments(ServerConfig config)
    {
        var arguments = new List<string>
        {
            "--data-root", config.Options.DataRoot,
            "--install-scope", config.Options.Scope.ToString().ToLowerInvariant()
        };
        if (config.Options.Quiet)
        {
            arguments.Add("--quiet");
        }

        arguments.AddRange(["serve", "--port", config.Port.ToString(CultureInfo.InvariantCulture)]);
        return [.. arguments];
    }
}
