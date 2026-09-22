using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;

namespace AiRaccoon.Settings;

/// <summary>
///     Acquires the settings server for a CLI process (ADR-0075 §5.1) with the proxy's launch
///     rule (F70/K1): by default it starts its own private backend through
///     <c>BackendLauncher.StartPrivateAsync</c> and trusts only the URL that child prints;
///     <c>--attach</c> opts into the legacy attach-or-start path on the configured port. Then
///     builds a <see cref="ServerSettingsStore" /> against the acquired endpoint. Every failure
///     mode — an undialable attach port, no answer within the acquire budget, a data root with no
///     minted token — is reported as <see cref="SettingsServerUnavailableException" />; a
///     wrong-but-present token is reported later, by <see cref="ServerSettingsStore" /> itself, as
///     <see cref="SettingsServerRefusedException" />.
/// </summary>
internal static class CliSettingsBackend
{
    /// <summary>
    ///     The production entry point: builds a real launcher, probe client and logger. Must stay
    ///     <c>async</c> rather than tail-return the inner call — the probe client has to live for the
    ///     whole poll loop BackendLauncher runs, not just until this method's synchronous return.
    /// </summary>
    internal static async Task<ISettingsStore> AcquireAsync(ServerConfig config, ILoggerFactory loggerFactory, CancellationToken ctx)
    {
        using var probeClient = new HttpClient();
        var launcher = new BackendLauncher(new ServerProbe(probeClient), BackendLauncher.DefaultBudget,
            TimeProvider.System, loggerFactory.CreateLogger<BackendLauncher>());
        return await AcquireAsync(launcher, Environment.ProcessPath, config, ctx);
    }

    /// <summary>
    ///     The testable core: takes the launcher and this process's path as seams, so a fake can stand
    ///     in for a real spawn and the verdict does not depend on how the calling process was launched.
    /// </summary>
    internal static async Task<ISettingsStore> AcquireAsync(IBackendLauncher launcher, string? processPath, ServerConfig config, CancellationToken ctx)
    {
        // The port only matters on the attach path: the private spawn pins --port 0, so a
        // configured 0 there is not an error (F70/K1).
        if (config.Attach && config.Port is < 1 or > 65535)
        {
            throw new SettingsServerUnavailableException(
                $"ai-raccoon: cannot dial --port {config.Port}: expected 1-65535, and 0 means \"any free port\"; pass a fixed --port, or drop --attach to start a private backend");
        }

        var executable = BackendLaunchArguments.Executable(processPath) ??
                         throw new SettingsServerUnavailableException($"ai-raccoon: {BackendLaunchArguments.UnavailableExecutableMessage(processPath, config)}");

        BackendResult acquired;
        try
        {
            // F70/K1: the default starts a private backend whose URL only this child can report;
            // the legacy attach-or-start path is reachable only through the explicit opt-in.
            acquired = config.Attach
                ? await launcher.AcquireAsync(config.Port, executable, BackendLaunchArguments.ServeArguments(config), ctx)
                : await launcher.StartPrivateAsync(executable, BackendLaunchArguments.PrivateServeArguments(config), ctx);
        }
        catch (BackendStartException ex)
        {
            throw new SettingsServerUnavailableException($"ai-raccoon: {ex.Message}", ex);
        }

        if (acquired.Url is null)
        {
            var reason = config.Attach
                ? $"no settings server at {ServerProbe.EndpointFor(config.Port)} (serve exit {acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})"
                : $"no settings server could be started on a private port (serve exit {acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})";
            throw new SettingsServerUnavailableException(
                $"ai-raccoon: {reason}" +
                (acquired.ServeStderr is { } stderr ? $" — stderr: {stderr}" : string.Empty));
        }

        var tokenFile = new McpTokenFile(config.Options.DataRoot);
        var token = tokenFile.Read() ?? throw new SettingsServerUnavailableException(
            config.Attach
                ? $"ai-raccoon: the backend at {acquired.Url} is listening but {tokenFile.Path} holds no token — a serve on another data root may own port {config.Port}"
                : $"ai-raccoon: the private backend at {acquired.Url} is listening but {tokenFile.Path} holds no token");

        return new ServerSettingsStore(new HttpClient { BaseAddress = new Uri(acquired.Url) }, token);
    }
}
