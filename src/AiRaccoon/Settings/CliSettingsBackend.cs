using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;

namespace AiRaccoon.Settings;

/// <summary>
///     Acquires the settings server for a CLI process (ADR-0075 §5.1): the legacy attach-or-start
///     acquire on <see cref="ServerConfig.Port" />, exactly as every server-routed verb (`settings
///     …`, `model …`, `watch registered`, `noise entries`, `repair`, …) used before F70/K1 and again
///     since owner ruling 2026-09-22 reverted this path off the private spawn it briefly carried —
///     "no own backend - attach - the same rules as usual" (ADR-0105). Whatever already answers the
///     configured port is trusted and reused; nothing here starts a private backend. The acquired
///     backend is shared and outlives this command under its own idle timeout (ruling N1); the only
///     change that ruling asked for on this path is disclosure — <see cref="Log.BackendOutlivesCommand" />
///     fires once the acquire has a live, token-checked store to hand back. Every failure mode — an
///     undialable port, no answer within the acquire budget, a data root with no minted token — is
///     reported as <see cref="SettingsServerUnavailableException" />; a wrong-but-present token is
///     reported later, by <see cref="ServerSettingsStore" /> itself, as
///     <see cref="SettingsServerRefusedException" />.
/// </summary>
internal static partial class CliSettingsBackend
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
        return await AcquireAsync(launcher, Environment.ProcessPath, config,
            loggerFactory.CreateLogger("AiRaccoon.Settings.CliSettingsBackend"), ctx);
    }

    /// <summary>
    ///     The testable core: takes the launcher and this process's path as seams, so a fake can stand
    ///     in for a real spawn and the verdict does not depend on how the calling process was launched.
    /// </summary>
    internal static async Task<ISettingsStore> AcquireAsync(IBackendLauncher launcher, string? processPath, ServerConfig config, ILogger logger, CancellationToken ctx)
    {
        if (config.Port is < 1 or > 65535)
        {
            throw new SettingsServerUnavailableException(
                $"ai-raccoon: cannot dial --port {config.Port}: expected 1-65535, and 0 means \"any free port\"; pass a fixed --port");
        }

        var executable = BackendLaunchArguments.Executable(processPath) ??
                         throw new SettingsServerUnavailableException($"ai-raccoon: {BackendLaunchArguments.UnavailableExecutableMessage(processPath, config)}");

        BackendResult acquired;
        try
        {
            acquired = await launcher.AcquireAsync(config.Port, executable, BackendLaunchArguments.ServeArguments(config), ctx);
        }
        catch (BackendStartException ex)
        {
            throw new SettingsServerUnavailableException($"ai-raccoon: {ex.Message}", ex);
        }

        if (acquired.Url is null)
        {
            throw new SettingsServerUnavailableException(
                $"ai-raccoon: no settings server at {ServerProbe.EndpointFor(config.Port)} " +
                $"(serve exit {acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})" +
                (acquired.ServeStderr is { } stderr ? $" — stderr: {stderr}" : string.Empty));
        }

        var tokenFile = new McpTokenFile(config.Options.DataRoot);
        var token = tokenFile.Read() ?? throw new SettingsServerUnavailableException(
            $"ai-raccoon: the backend at {acquired.Url} is listening but {tokenFile.Path} holds no token " +
            $"— a serve on another data root may own port {config.Port}");

        // F38 residual (owner ruling N1, 2026-09-22): the 4h idle default stays intended, so this
        // is disclosure only — until now the only word this command ever said about the backend was
        // that it was starting, never that it survives this process, nor how to stop it.
        Log.BackendOutlivesCommand(logger, config.Port);
        return new ServerSettingsStore(new HttpClient { BaseAddress = new Uri(acquired.Url) }, token);
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 687, Level = LogLevel.Information,
            Message = "ai-raccoon: the backend on port {Port} keeps running after this command exits, under its own idle timeout — stop it with ai-raccoon serve --restart --attach --port {Port}")]
        public static partial void BackendOutlivesCommand(ILogger logger, int port);
    }
}
