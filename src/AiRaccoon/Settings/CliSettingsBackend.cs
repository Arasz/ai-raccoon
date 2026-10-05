using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;

namespace AiRaccoon.Settings;

/// <summary>
///     Acquires the settings server for a CLI process (ADR-0075 §5.1): attach-or-start behind the
///     identity proof (ADR-0106). A proven listener on <see cref="ServerConfig.Port" /> is reused,
///     confirmed connection refusal starts one there, and an unproven holder causes refusal. The acquired
///     backend is shared and outlives this command under its own idle timeout (ruling N1); the
///     disclosure line (<see cref="Log.BackendOutlivesCommand" />) fires once the acquire has a
///     live, token-checked store to hand back. An undialable --port is an <see cref="UndialablePortException" />;
///     every other failure mode — no answer within the acquire budget, a data root with no minted token — is reported as
///     <see cref="SettingsServerUnavailableException" />; a wrong-but-present token is reported
///     later, by <see cref="ServerSettingsStore" /> itself, as <see cref="SettingsServerRefusedException" />.
/// </summary>
internal static partial class CliSettingsBackend
{
    /// <summary>How long an ordinary settings call waits before the server counts as not answering.</summary>
    internal static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(100);

    /// <summary>
    ///     The production entry point: builds a real launcher, probe client and logger. Must stay
    ///     <c>async</c> rather than tail-return the inner call — the probe client has to live for the
    ///     whole poll loop BackendLauncher runs, not just until this method's synchronous return.
    /// </summary>
    internal static async Task<ISettingsStore> AcquireAsync(ServerConfig config, ILoggerFactory loggerFactory, CancellationToken ctx)
    {
        using var probeClient = new HttpClient();
        var probe = new ServerProbe(probeClient);
        var launcher = new BackendLauncher(probe, BackendLauncher.DefaultBudget,
            TimeProvider.System, loggerFactory.CreateLogger<BackendLauncher>());
        using var proverClient = new HttpClient();
        var prover = new IdentityProver(config.Options, proverClient);
        return await AcquireAsync(launcher, prover, probe, Environment.ProcessPath, config,
            loggerFactory.CreateLogger("AiRaccoon.Settings.CliSettingsBackend"), ctx);
    }

    /// <summary>
    ///     The testable core: takes the launcher, the verifier, the probe and this process's path as
    ///     seams, so a fake can stand in for a real spawn and the verdict does not depend on how the
    ///     calling process was launched.
    /// </summary>
    internal static async Task<ISettingsStore> AcquireAsync(IBackendLauncher launcher, IIdentityProver prover,
        IServerProbe probe, string? processPath, ServerConfig config, ILogger logger, CancellationToken ctx)
    {
        if (config.Port is < 1 or > 65535)
        {
            throw new UndialablePortException(
                $"cannot dial --port {config.Port}: expected 1-65535, and 0 means \"any free port\"; pass a fixed --port");
        }

        BankPresenceGuard.EnsureExists(config.Options);

        var executable = BackendLaunchArguments.Executable(processPath) ??
                         throw new SettingsServerUnavailableException(ErrorCode.Reach.AutoStartUnsupported,
                             $"ai-raccoon: {BackendLaunchArguments.UnavailableExecutableMessage(processPath, config)}");

        BackendSessions.AcquireOutcome acquired;
        try
        {
            acquired = await BackendSessions.AcquireSharedAsync(probe, prover, launcher, executable, config,
                logger, ctx);
        }
        catch (BackendStartException ex)
        {
            throw new SettingsServerUnavailableException(ErrorCode.Reach.StartFailed, $"ai-raccoon: {ex.Message}", ex);
        }

        if (acquired.Result.Url is null)
        {
            if (acquired.ProofFailure is { } failure)
            {
                throw new SettingsServerUnavailableException(ErrorCode.Server.Unproven, BackendSessions.Refusal(config.Port, failure));
            }

            throw new SettingsServerUnavailableException(ErrorCode.Reach.Unavailable,
                $"ai-raccoon: no settings server at {ServerProbe.EndpointFor(config.Port)} " +
                $"(serve exit {acquired.Result.ServeExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})" +
                (acquired.Result.ServeStderr is { } stderr ? $" — stderr: {stderr}" : string.Empty));
        }

        // ADR-0107 PC.4: acquired.Result.Url is only ever non-null once identity is already proven
        // for both attach and start, so "another data root" never applies
        // here — RefusalReason (e.g. a chmod-700 remedy) is the real, actionable cause when set.
        var tokenFile = new McpTokenFile(config.Options);
        var token = tokenFile.Read() ?? throw new SettingsServerUnavailableException(ErrorCode.Server.NoToken,
            $"ai-raccoon: the backend at {acquired.Result.Url} is listening but has no usable token for this data root " +
            $"({tokenFile.RefusalReason ?? $"{tokenFile.Path} holds no token yet"})");

        // F38 residual (owner ruling N1, 2026-09-22): the idle default stays intended, so this is
        // disclosure only — the backend survives this command; the line names the port that actually
        // holds it and how to stop it.
        Log.BackendOutlivesCommand(logger, new Uri(acquired.Result.Url).Port);
        return new ServerSettingsStore(CreateClient(acquired.Result.Url), token, RequestDeadline);
    }

    /// <summary>
    ///     The client every settings call goes through. It has no timeout of its own: the store bounds
    ///     each call with <see cref="RequestDeadline" />, and a repair report's bank-wide scan runs unbounded.
    /// </summary>
    internal static HttpClient CreateClient(string url) =>
        new() { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };

    internal static partial class Log
    {
        [LoggerMessage(EventId = 687, Level = LogLevel.Information,
            Message = "ai-raccoon: the backend on port {Port} keeps running after this command exits, under its own idle timeout — stop it with ai-raccoon serve --restart --port {Port}")]
        public static partial void BackendOutlivesCommand(ILogger logger, int port);
    }
}
