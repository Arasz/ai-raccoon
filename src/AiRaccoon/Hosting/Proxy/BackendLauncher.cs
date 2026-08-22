using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AiRaccoon.Hosting.Common;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>Raised when the backend binary could not be launched at all; carries the operator's reason.</summary>
internal sealed class BackendStartException(string message, Exception inner) : Exception(message, inner);

/// <summary>
///     Acquires a live ai-raccoon HTTP backend for the proxy (ADR-0020): probe first, else start
///     `ai-raccoon serve` and poll the probe until it answers or the budget expires. Never kills,
///     signals or terminates the backend — lifetime belongs to IdleWatchdog alone.
/// </summary>
internal sealed partial class BackendLauncher : IBackendLauncher
{
    public const string BackendSessionClient = "BackendSessionClient";

    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Stderr is captured for the failure path only, bounded to its last ~4 KB.</summary>
    private const int StderrCaptureCharLimit = 4096;

    /// <summary>Bounds the re-probe that follows an exited backend, which the spent budget cannot.</summary>
    private static readonly TimeSpan LastChanceBudget = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _budget;
    private readonly ILogger _logger;
    private readonly IServerProbe _probe;
    private readonly TimeProvider _timeProvider;

    public BackendLauncher(IServerProbe serverProbe, TimeSpan budget, TimeProvider timeProvider, ILogger<BackendLauncher> logger)
    {
        _probe = serverProbe;
        _logger = logger;
        _budget = budget;
        _timeProvider = timeProvider;
        Guard.IsGreaterThan(_budget, TimeSpan.Zero);
    }

    /// <summary>Returns the backend URL on the port, starting the given command when nothing answers.</summary>
    public async Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments,
        CancellationToken ctx)
    {
        Guard.IsNotNullOrWhiteSpace(fileName);
        Guard.IsNotNull(arguments);

        var url = ServerProbe.EndpointFor(port).ToString();
        if (await _probe.RespondsAsync(port, ctx))
        {
            Log.BackendLive(_logger, url);
            return new BackendResult(url, null);
        }

        Log.StartingBackend(_logger, port);
        var (backend, stderr) = Start(fileName, arguments);

        // A cold start pays the encryption-key resolve, the bank decrypt probe and the ONNX model
        // load, so a first probe miss is expected: poll until it answers or the budget expires.
        using var budget = new CancellationTokenSource(_budget, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ctx, budget.Token);
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
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ctx.IsCancellationRequested)
        {
            // The budget expired, not the caller's token: report the failure rather than throwing.
        }

        if (exited)
        {
            // One last probe: another starter may have won the port between the two. It gets its own
            // bound, because the budget token may already be spent.
            ctx.ThrowIfCancellationRequested();
            if (await ProbeWithinAsync(port, LastChanceBudget, ctx))
            {
                Log.BackendLive(_logger, url);
                return new BackendResult(url, null);
            }
        }

        return GaveUp(url, backend.HasExited ? backend.ExitCode : null, stderr.Snapshot());
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

    private BackendResult GaveUp(string url, int? serveExitCode, string? serveStderr)
    {
        Log.BackendUnavailable(_logger, url, (int)_budget.TotalSeconds, serveExitCode, serveStderr ?? string.Empty);
        return new BackendResult(null, serveExitCode, serveStderr);
    }

    /// <summary>
    ///     Starts the backend with all three pipes redirected and both output pipes drained on
    ///     background tasks: the proxy's own stdout is the JSON-RPC channel, and an unread pipe
    ///     buffer blocks the child. Stdout stays discarded — it is not the proxy's to relay — but
    ///     stderr is captured (bounded) so a failure can report why, not just an exit code.
    /// </summary>
    private static (Process Backend, TailCapture Stderr) Start(string fileName, IReadOnlyList<string> arguments)
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
        var stderr = new TailCapture(StderrCaptureCharLimit);
        _ = CaptureAsync(backend.StandardError, stderr);
        return (backend, stderr);
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

    private static async Task CaptureAsync(TextReader pipe, TailCapture capture)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await pipe.ReadAsync(buffer)) > 0)
            {
                capture.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed with the backend; nothing left to capture.
        }
    }

    /// <summary>Thread-safe accumulator keeping only the last <paramref name="maxChars"/> characters
    /// written, readable at any time — the backend may still be running when a snapshot is taken.</summary>
    private sealed class TailCapture(int maxChars)
    {
        private readonly StringBuilder _buffer = new();
        private readonly Lock _gate = new();

        public void Append(ReadOnlySpan<char> chars)
        {
            lock (_gate)
            {
                _buffer.Append(chars);
                if (_buffer.Length > maxChars)
                {
                    _buffer.Remove(0, _buffer.Length - maxChars);
                }
            }
        }

        public string? Snapshot()
        {
            lock (_gate)
            {
                return _buffer.Length == 0 ? null : _buffer.ToString();
            }
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 633, Level = LogLevel.Information, Message = "ai-raccoon: starting the backend on port {Port}")]
        public static partial void StartingBackend(ILogger logger, int port);

        [LoggerMessage(EventId = 634, Level = LogLevel.Debug, Message = "ai-raccoon: backend live at {Url}")]
        public static partial void BackendLive(ILogger logger, string url);

        [LoggerMessage(EventId = 635, Level = LogLevel.Error,
            Message = "ai-raccoon: the backend at {Url} did not answer within {BudgetSeconds}s (serve exit {ServeExitCode}) stderr: {ServeStderr}")]
        public static partial void BackendUnavailable(ILogger logger, string url, int budgetSeconds, int? serveExitCode, string serveStderr);
    }
}
