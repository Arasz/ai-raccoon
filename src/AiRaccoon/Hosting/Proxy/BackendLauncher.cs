using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AiRaccoon.Hosting.Common;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>Raised when the backend binary could not be launched at all; carries the operator's reason.</summary>
internal sealed class BackendStartException(string message, Exception inner) : Exception(message, inner);

/// <summary>
///     Acquires a live ai-raccoon HTTP backend for the proxy (ADR-0020). The default path is
///     private spawn (F70/K1): start `ai-raccoon serve --port 0` and trust only the URL that child
///     prints, so no pre-existing listener is ever contacted. The explicit attach path keeps the
///     legacy behaviour: probe first, else start `serve` on the port and poll. Never kills, signals
///     or terminates the backend — lifetime belongs to IdleWatchdog alone.
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

    /// <summary>
    ///     Starts a private backend and returns the URL it printed. The launch arguments carry
    ///     <c>--port 0</c>, so the OS picks the port; the URL comes only from this child's stdout,
    ///     never from a probe of a port anything else could already hold (F70/K1).
    /// </summary>
    public async Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments,
        CancellationToken ctx)
    {
        Guard.IsNotNullOrWhiteSpace(fileName);
        Guard.IsNotNull(arguments);

        Log.StartingPrivateBackend(_logger);
        var (backend, stderr, urlLine) = Start(fileName, arguments);

        using var budget = new CancellationTokenSource(_budget, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ctx, budget.Token);
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            while (!urlLine.IsCompletedSuccessfully && !backend.HasExited)
            {
                await timer.WaitForNextTickAsync(waiting.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ctx.IsCancellationRequested)
        {
            // The budget expired, not the caller's token: report the failure rather than throwing.
        }

        // The last check also covers a URL that arrived exactly as the budget expired: the child
        // printed it after binding, so the backend is live whatever the clock says.
        if (urlLine.IsCompletedSuccessfully && urlLine.Result is { } reported)
        {
            Log.BackendLive(_logger, reported);
            return new BackendResult(reported, null);
        }

        var exitCode = backend.HasExited ? backend.ExitCode : (int?)null;
        var captured = stderr.Snapshot();
        Log.PrivateBackendUnavailable(_logger, (int)_budget.TotalSeconds, exitCode, captured ?? string.Empty);
        return new BackendResult(null, exitCode, captured);
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
        var (backend, stderr, _) = Start(fileName, arguments);

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
    ///     buffer blocks the child. Stdout stays out of the proxy's stdout — the URL it reports is
    ///     read on the pipe and handed back as a task, not relayed — and stderr is captured (bounded)
    ///     so a failure can report why, not just an exit code.
    /// </summary>
    private static (Process Backend, TailCapture Stderr, Task<string?> UrlLine) Start(string fileName, IReadOnlyList<string> arguments)
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

        var urlLine = WatchUrlLineAsync(backend.StandardOutput);
        var stderr = new TailCapture(StderrCaptureCharLimit);
        _ = CaptureAsync(backend.StandardError, stderr);
        return (backend, stderr, urlLine);
    }

    /// <summary>
    ///     Completes with the first loopback MCP URL the child prints (serve prints exactly one
    ///     line after binding) and keeps draining its stdout afterwards. The URL can only come from
    ///     this child's pipe, which is what makes the private path attach-proof.
    /// </summary>
    private static Task<string?> WatchUrlLineAsync(TextReader pipe)
    {
        var reported = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            // Discarded after the URL is found: the backend's output is not the proxy's to relay,
            // and an unread pipe would block the child.
            string? url = null;
            try
            {
                while (await pipe.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (url is null && TryParseBackendUrl(line) is { } parsed)
                    {
                        url = parsed;
                        reported.TrySetResult(parsed);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The pipe closed with the backend; nothing left to read.
            }

            // EOF without a URL still completes the task, so the budget loop can tell "no more
            // output" from "still waiting" rather than polling the file handle.
            reported.TrySetResult(url);
        });
        return reported.Task;
    }

    private static string? TryParseBackendUrl(string line)
    {
        if (!Uri.TryCreate(line.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.AbsolutePath == "/mcp"
            ? uri.ToString()
            : null;
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
        [LoggerMessage(EventId = 631, Level = LogLevel.Information,
            Message = "ai-raccoon: starting a private backend on an ephemeral port")]
        public static partial void StartingPrivateBackend(ILogger logger);

        [LoggerMessage(EventId = 633, Level = LogLevel.Information, Message = "ai-raccoon: starting the backend on port {Port}")]
        public static partial void StartingBackend(ILogger logger, int port);

        [LoggerMessage(EventId = 634, Level = LogLevel.Debug, Message = "ai-raccoon: backend live at {Url}")]
        public static partial void BackendLive(ILogger logger, string url);

        [LoggerMessage(EventId = 635, Level = LogLevel.Error,
            Message = "ai-raccoon: the backend at {Url} did not answer within {BudgetSeconds}s (serve exit {ServeExitCode}) stderr: {ServeStderr}")]
        public static partial void BackendUnavailable(ILogger logger, string url, int budgetSeconds, int? serveExitCode, string serveStderr);

        [LoggerMessage(EventId = 632, Level = LogLevel.Error,
            Message = "ai-raccoon: the private backend did not report a URL within {BudgetSeconds}s (serve exit {ServeExitCode}) stderr: {ServeStderr}")]
        public static partial void PrivateBackendUnavailable(ILogger logger, int budgetSeconds, int? serveExitCode, string serveStderr);
    }
}
