using System.Globalization;

namespace AiRaccoon.Hosting.Common;

/// <summary>
///     The launch identity a caller hands the backend launcher to acquire an
///     <c>ai-raccoon serve</c>: the flags every backend needs regardless of who is acquiring it
///     (the proxy, or a CLI settings command) precede the verb and carry the configured port.
/// </summary>
internal static partial class BackendLaunchArguments
{
    /// <summary>The dotnet host's own file name: what `Environment.ProcessPath` names under `dotnet run`,
    /// `dotnet exec`, or a dotnet-tool published without an apphost (a `dotnet &lt;dll&gt;` shim).</summary>
    private const string DotnetMuxerFileName = "dotnet";

    /// <summary>This binary's own filename on the dotnet global-tool shim and on PATH (".exe" on Windows).</summary>
    internal static string ExecutableFileName => OperatingSystem.IsWindows() ? "ai-raccoon.exe" : "ai-raccoon";

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
            ? $"this process was started through the dotnet host, which cannot auto-start a backend; start the server manually first: ai-raccoon {string.Join(' ', ServeArguments(config))}, then retry"
            : "the running executable path is unknown";

    public static string[] ServeArguments(ServerConfig config) => Arguments(config, config.Port);

    private static string[] Arguments(ServerConfig config, int port)
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

        arguments.AddRange(["serve", "--port", port.ToString(CultureInfo.InvariantCulture)]);
        return [.. arguments];
    }

    /// <summary>
    ///     The dotnet global-tool shim's own path (<c>~/.dotnet/tools/ai-raccoon[.exe]</c>), or null
    ///     when <paramref name="userProfileDirectory" /> is unknown.
    /// </summary>
    internal static string? GlobalToolShimPath(string? userProfileDirectory) =>
        string.IsNullOrEmpty(userProfileDirectory) ? null : Path.Combine(userProfileDirectory, ".dotnet", "tools", ExecutableFileName);

    /// <summary>The first directory on <paramref name="pathVariable" /> whose <see cref="ExecutableFileName" /> <paramref name="fileExists" />, or null when none does.</summary>
    internal static string? PathExecutable(string? pathVariable, Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, ExecutableFileName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     <paramref name="own" /> when it still exists. Otherwise — and only when it is the
    ///     process's own executable (<paramref name="currentProcessPath" />), deleted from under a
    ///     still-running proxy by <c>dotnet tool update</c> (ADR-0116) — the follow-up fallback: the
    ///     dotnet global-tool shim, then <c>PATH</c>. An explicitly named executable that is gone is
    ///     never swapped for another binary: it is returned unchanged and fails to start as named.
    ///     Neither fallback found, <paramref name="own" /> is returned unchanged so the launcher's
    ///     existing refusal applies exactly as before. A fallback in use is logged once.
    /// </summary>
    internal static string ResolveExecutable(
        string own, string? currentProcessPath, ILogger logger, Func<string, bool> fileExists, string? userProfileDirectory, string? pathVariable)
    {
        if (fileExists(own))
        {
            return own;
        }

        // ADR-0116's remit is exactly one path: the executable this process was started from. A
        // caller-named alternative that no longer exists is a launch failure, not a licence to
        // spawn whatever ai-raccoon the shim or PATH happens to offer.
        if (!string.Equals(own, currentProcessPath, StringComparison.Ordinal))
        {
            return own;
        }

        var shim = GlobalToolShimPath(userProfileDirectory);
        if (shim is not null && fileExists(shim))
        {
            Log.ExecutableFallback(logger, own, shim);
            return shim;
        }

        var onPath = PathExecutable(pathVariable, fileExists);
        if (onPath is null)
        {
            return own;
        }

        Log.ExecutableFallback(logger, own, onPath);
        return onPath;
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 693, Level = LogLevel.Information,
            Message = "ai-raccoon: this process's own executable at '{OwnPath}' is gone, most likely replaced by 'dotnet tool update'; spawning '{Fallback}' instead")]
        public static partial void ExecutableFallback(ILogger logger, string ownPath, string fallback);
    }
}
