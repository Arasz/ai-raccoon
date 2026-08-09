using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Setup.Serve;

/// <summary>The live backend URL, or null with the `serve` exit code when it never answered.</summary>
internal readonly record struct BackendResult(string? Url, int? ServeExitCode);

/// <summary>Raised when the backend binary could not be launched at all; carries the operator's reason.</summary>
internal sealed class BackendStartException(string message, Exception inner) : Exception(message, inner);

/// <summary>
///     Acquires a live ai-raccoon HTTP backend for the proxy (ADR-0020): probe first, else start
///     `ai-raccoon serve` and poll the probe until it answers or the budget expires. Never kills,
///     signals or terminates the backend — lifetime belongs to IdleWatchdog alone.
/// </summary>
internal sealed partial class BackendLauncher
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounds the re-probe that follows an exited backend, which the spent budget cannot.</summary>
    private static readonly TimeSpan LastChanceBudget = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _budget;
    private readonly ILogger _logger;
    private readonly ServerProbe _probe;
    private readonly TimeProvider _timeProvider;

    public BackendLauncher(ServerProbe probe, ILogger logger, TimeSpan? budget = null, TimeProvider? timeProvider = null)
    {
        Guard.IsNotNull(probe);
        Guard.IsNotNull(logger);
        _probe = probe;
        _logger = logger;
        _budget = budget ?? DefaultBudget;
        _timeProvider = timeProvider ?? TimeProvider.System; // test seam: fake clock for the budget and the poll tick
        Guard.IsGreaterThan(_budget, TimeSpan.Zero);
    }

    /// <summary>Returns the backend URL on the port, starting the given command when nothing answers.</summary>
    public async Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNullOrWhiteSpace(fileName);
        Guard.IsNotNull(arguments);

        var url = ServerProbe.EndpointFor(port).ToString();
        if (await _probe.RespondsAsync(port, cancellationToken))
        {
            Log.BackendLive(_logger, url);
            return new BackendResult(url, null);
        }

        Log.StartingBackend(_logger, port);
        var backend = Start(fileName, arguments);

        // A cold start pays the encryption-key resolve, the bank decrypt probe and the ONNX model
        // load, so a first probe miss is expected: poll until it answers or the budget expires.
        using var budget = new CancellationTokenSource(_budget, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        var exited = false;
        try
        {
            while (await timer.WaitForNextTickAsync(waiting.Token))
            {
                if (await _probe.RespondsAsync(port, waiting.Token))
                {
                    Log.BackendLive(_logger, url);
                    return new BackendResult(url, null);
                }

                if (backend.HasExited)
                {
                    exited = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The budget expired, not the caller's token: report the failure rather than throwing.
        }

        if (exited)
        {
            // One last probe: another starter may have won the port between the two. It gets its own
            // bound, because the budget token may already be spent.
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProbeWithinAsync(port, LastChanceBudget, cancellationToken))
            {
                Log.BackendLive(_logger, url);
                return new BackendResult(url, null);
            }
        }

        return GaveUp(url, backend.HasExited ? backend.ExitCode : null);
    }

    /// <summary>A probe under its own bound: the bound expiring is a miss, the caller's token is not.</summary>
    private async Task<bool> ProbeWithinAsync(int port, TimeSpan bound, CancellationToken cancellationToken)
    {
        using var boundary = new CancellationTokenSource(bound, _timeProvider);
        using var probing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, boundary.Token);
        try
        {
            return await _probe.RespondsAsync(port, probing.Token);
        }
        catch (OperationCanceledException) when (boundary.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private BackendResult GaveUp(string url, int? serveExitCode)
    {
        Log.BackendUnavailable(_logger, url, (int)_budget.TotalSeconds, serveExitCode);
        return new BackendResult(null, serveExitCode);
    }

    /// <summary>
    ///     Starts the backend with all three pipes redirected and both output pipes drained on
    ///     background tasks: the proxy's own stdout is the JSON-RPC channel, and an unread pipe
    ///     buffer blocks the child.
    /// </summary>
    private static Process Start(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process backend;
        try
        {
            backend = Process.Start(startInfo)!;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            throw new BackendStartException($"could not start {fileName} ({ex.Message})", ex);
        }

        _ = DrainAsync(backend.StandardOutput);
        _ = DrainAsync(backend.StandardError);
        return backend;
    }

    private static async Task DrainAsync(TextReader pipe)
    {
        try
        {
            var buffer = new char[4096];
            while (await pipe.ReadAsync(buffer) > 0)
            {
                // Discarded: the backend's own output is not the proxy's to relay.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed with the backend; nothing left to drain.
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 633, Level = LogLevel.Information, Message = "ai-raccoon: starting the backend on port {Port}")]
        public static partial void StartingBackend(ILogger logger, int port);

        [LoggerMessage(EventId = 634, Level = LogLevel.Debug, Message = "ai-raccoon: backend live at {Url}")]
        public static partial void BackendLive(ILogger logger, string url);

        [LoggerMessage(EventId = 635, Level = LogLevel.Error,
            Message = "ai-raccoon: the backend at {Url} did not answer within {BudgetSeconds}s (serve exit {ServeExitCode})")]
        public static partial void BackendUnavailable(ILogger logger, string url, int budgetSeconds, int? serveExitCode);
    }
}
