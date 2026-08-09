using CommunityToolkit.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Setup.Serve;

/// <summary>
///     Builds the incoming-message filter that relays a client's traffic to one backend session,
///     switching on JSON-RPC message kind only (docs/adr/0020-always-on-http-stdio-proxy.md).
/// </summary>
internal static partial class ProxyForwarder
{
    /// <summary>
    ///     Relays requests and notifications to the backend, suppressing the local handlers; any other
    ///     message kind falls through to them. A lost connection re-acquires and retries once.
    /// </summary>
    internal static McpMessageFilter Create(McpSession backend,
        Func<CancellationToken, Task<McpSession>> reacquire, ILogger logger)
    {
        Guard.IsNotNull(backend);
        Guard.IsNotNull(reacquire);
        Guard.IsNotNull(logger);
        var channel = new BackendChannel(backend, reacquire);
        return next => async (context, cancellationToken) =>
        {
            switch (context.JsonRpcMessage)
            {
                case JsonRpcRequest request:
                    Log.RequestRelayed(logger, request.Method);
                    // A fresh message: the incoming one carries this session's transport, which would
                    // send the relayed copy straight back to the client instead of to the backend.
                    var answer = await RelayAsync(channel, logger, request.Method,
                        session => session.SendRequestAsync(
                            new JsonRpcRequest { Id = request.Id, Method = request.Method, Params = request.Params },
                            cancellationToken), cancellationToken);
                    // The client correlates on the id it chose, so restore it rather than trusting the
                    // backend's echo. context.Server is bound to the transport the request arrived on
                    // and rejects a caller-supplied context, so the answer carries none.
                    await context.Server.SendMessageAsync(
                        new JsonRpcResponse { Id = request.Id, Result = answer.Result }, cancellationToken);
                    return;
                case JsonRpcNotification notification:
                    Log.NotificationRelayed(logger, notification.Method);
                    await RelayAsync(channel, logger, notification.Method, async session =>
                    {
                        await session.SendMessageAsync(
                            new JsonRpcNotification { Method = notification.Method, Params = notification.Params },
                            cancellationToken);
                        return true;
                    }, cancellationToken);
                    return;
                default:
                    await next(context, cancellationToken);
                    return;
            }
        };
    }

    private static async Task<TAnswer> RelayAsync<TAnswer>(BackendChannel channel, ILogger logger, string method,
        Func<McpSession, Task<TAnswer>> send, CancellationToken cancellationToken)
    {
        var session = channel.Current;
        try
        {
            return await send(session);
        }
        catch (Exception ex) when (LostTheBackend(ex))
        {
            Log.BackendLost(logger, method, ex);
            var replacement = await ReplaceAsync(channel, logger, method, session, cancellationToken);
            try
            {
                return await send(replacement);
            }
            catch (Exception retry) when (retry is not McpException and not OperationCanceledException)
            {
                throw Failed(logger, method, retry);
            }
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            throw Failed(logger, method, ex);
        }
    }

    private static async Task<McpSession> ReplaceAsync(BackendChannel channel, ILogger logger, string method,
        McpSession lost, CancellationToken cancellationToken)
    {
        try
        {
            return await channel.ReplaceAsync(lost, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Failed(logger, method, ex);
        }
    }

    /// <summary>
    ///     True when nothing answered. A JSON-RPC error is an answer, and so is an HTTP status —
    ///     both mean the backend is alive and must not be re-acquired.
    /// </summary>
    private static bool LostTheBackend(Exception ex) => ex switch
    {
        McpException => false,
        OperationCanceledException => false,
        HttpRequestException http => http.StatusCode is null,
        _ => true
    };

    /// <summary>
    ///     Without this the client is told only "An error occurred.": the SDK collapses every non-MCP
    ///     exception to that, and the backend's own text is the whole diagnostic.
    /// </summary>
    private static McpProtocolException Failed(ILogger logger, string method, Exception ex)
    {
        Log.RelayFailed(logger, method, ex);
        return new McpProtocolException($"backend relay failed: {ex.Message}", ex, McpErrorCode.InternalError);
    }

    /// <summary>Holds the session in use and swaps it once per loss, however many calls saw that loss.</summary>
    private sealed class BackendChannel
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Func<CancellationToken, Task<McpSession>> _reacquire;
        private McpSession _current;

        public BackendChannel(McpSession backend, Func<CancellationToken, Task<McpSession>> reacquire)
        {
            _current = backend;
            _reacquire = reacquire;
        }

        public McpSession Current => Volatile.Read(ref _current);

        public async Task<McpSession> ReplaceAsync(McpSession lost, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Concurrent callers all fail on the same dead session; only the first re-acquires.
                if (!ReferenceEquals(Current, lost))
                {
                    return Current;
                }

                var replacement = await _reacquire(cancellationToken);
                Volatile.Write(ref _current, replacement);
                return replacement;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 636, Level = LogLevel.Debug, Message = "ai-raccoon: relaying {Method} to the backend")]
        public static partial void RequestRelayed(ILogger logger, string method);

        [LoggerMessage(EventId = 637, Level = LogLevel.Debug,
            Message = "ai-raccoon: relaying notification {Method} to the backend")]
        public static partial void NotificationRelayed(ILogger logger, string method);

        [LoggerMessage(EventId = 638, Level = LogLevel.Error,
            Message = "ai-raccoon: relaying {Method} to the backend failed")]
        public static partial void RelayFailed(ILogger logger, string method, Exception exception);

        [LoggerMessage(EventId = 639, Level = LogLevel.Warning,
            Message = "ai-raccoon: the backend did not answer {Method}; re-acquiring once")]
        public static partial void BackendLost(ILogger logger, string method, Exception exception);
    }
}
