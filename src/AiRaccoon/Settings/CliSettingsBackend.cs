using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;

namespace AiRaccoon.Settings;

/// <summary>
///     Acquires the settings server for a CLI process (ADR-0075 §5.1): reuses
///     <c>BackendLauncher.AcquireAsync</c> exactly as the stdio proxy does
///     (<see cref="BackendLaunchArguments" />), then builds a <see cref="ServerSettingsStore" />
///     against the acquired endpoint. Every failure mode — an undialable port, no answer within the
///     acquire budget, a data root with no minted token — is reported as
///     <see cref="SettingsServerUnavailableException" />; a wrong-but-present token is reported later,
///     by <see cref="ServerSettingsStore" /> itself, as <see cref="SettingsServerRefusedException" />.
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
        return await AcquireAsync(launcher, config, ctx);
    }

    /// <summary>The testable core: takes the launcher as a seam so a fake can stand in for a real spawn.</summary>
    internal static async Task<ISettingsStore> AcquireAsync(IBackendLauncher launcher, ServerConfig config, CancellationToken ctx)
    {
        if (config.Port is < 1 or > 65535)
        {
            throw new SettingsServerUnavailableException(
                $"ai-raccoon: cannot dial --port {config.Port}: expected 1-65535, and 0 means \"any free port\"; pass a fixed --port");
        }

        var executable = BackendLaunchArguments.Executable() ??
            throw new SettingsServerUnavailableException("ai-raccoon: the running executable path is unknown");

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
                $"(serve exit {acquired.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})");
        }

        var tokenFile = new McpTokenFile(config.Options.DataRoot);
        var token = tokenFile.Read() ?? throw new SettingsServerUnavailableException(
            $"ai-raccoon: the backend at {acquired.Url} is listening but {tokenFile.Path} holds no token " +
            $"— a serve on another data root may own port {config.Port}");

        return new ServerSettingsStore(new HttpClient { BaseAddress = new Uri(acquired.Url) }, token);
    }
}
