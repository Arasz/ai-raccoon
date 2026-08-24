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
    /// <summary>The dotnet host's own file name: what `Environment.ProcessPath` names under `dotnet run`,
    /// `dotnet exec`, or a dotnet-tool published without an apphost (a `dotnet &lt;dll&gt;` shim).</summary>
    private const string DotnetMuxerFileName = "dotnet";

    /// <summary>
    ///     This very binary: the backend is another ai-raccoon, started as `serve`. Null when the
    ///     path is unknown, or this is an unpackaged invocation (<see cref="IsUnpackagedInvocation(string?)" />)
    ///     — spawning the dotnet muxer as `&lt;muxer&gt; serve` cannot start a backend.
    /// </summary>
    public static string? Executable() => Executable(Environment.ProcessPath);

    internal static string? Executable(string? processPath) => IsUnpackagedInvocation(processPath) ? null : processPath;

    /// <summary>
    ///     True when <paramref name="processPath" /> names the dotnet muxer rather than a packaged
    ///     apphost: the muxer does not understand ai-raccoon's own CLI shape, so it cannot serve as
    ///     the auto-started backend.
    /// </summary>
    internal static bool IsUnpackagedInvocation(string? processPath) =>
        processPath is not null &&
        string.Equals(Path.GetFileNameWithoutExtension(processPath), DotnetMuxerFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The reason a caller reports when <see cref="Executable()" /> returns null (no leading
    /// "ai-raccoon: " — callers own their own prefix): names the unpackaged shape and the manual
    /// `serve` command when that is the reason, else stays generic.</summary>
    public static string UnavailableExecutableMessage(ServerConfig config) => UnavailableExecutableMessage(Environment.ProcessPath, config);

    internal static string UnavailableExecutableMessage(string? processPath, ServerConfig config) =>
        IsUnpackagedInvocation(processPath)
            ? "this process was started through the dotnet host, which cannot auto-start a backend; " +
              $"start the server manually first: ai-raccoon {string.Join(' ', ServeArguments(config))}, then retry"
            : "the running executable path is unknown";

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
