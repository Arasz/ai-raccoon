using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     Builds the incoming-message filter that relays a client's traffic to one backend session,
///     switching on JSON-RPC message kind only (docs/adr/0020-always-on-http-stdio-proxy.md).
/// </summary>
internal partial class ProxyForwarder(ILogger<ProxyForwarder> logger) : IProxyForwarder
{
    /// <summary>
    ///     Relays requests and notifications to the backend, suppressing the local handlers; any other
    ///     message kind falls through to them. A lost connection re-acquires and retries once.
    /// </summary>
    public McpMessageFilter Create(McpSession backend, IBackendSessions backendSessions)
    {
        var channel = new BackendChannel(backend, backendSessions);
        return next => async (context, cancellationToken) =>
        {
            switch (context.JsonRpcMessage)
            {
                case JsonRpcRequest request:
                    Log.RequestRelayed(logger, request.Method);
                    var answer = await RelayAsync(channel, logger, request.Method, RevisionOf(request),
                        session => session.SendRequestAsync(
                            new JsonRpcRequest { Id = request.Id, Method = request.Method, Params = request.Params },
                            cancellationToken), cancellationToken);
                    await context.Server.SendMessageAsync(
                        new JsonRpcResponse { Id = request.Id, Result = answer.Result }, cancellationToken);
                    return;
                case JsonRpcNotification notification:
                    Log.NotificationRelayed(logger, notification.Method);
                    await RelayAsync(channel, logger, notification.Method, null, async session =>
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
        string? revision, Func<McpSession, Task<TAnswer>> send, CancellationToken cancellationToken)
    {
        McpSession session;
        try
        {
            session = await channel.SpeakingAsync(revision, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Failed(logger, method, ex);
        }

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
    ///     The protocol revision this request speaks, or null when it does not say. Newer clients
    ///     declare it per request and the SDK lifts it into the context; a handshake carries it in the
    ///     body, which is the only place it appears before a revision has been agreed.
    /// </summary>
    private static string? RevisionOf(JsonRpcRequest request)
    {
        if (request.Context?.ProtocolVersion is { } declared)
        {
            return declared;
        }

        return (request.Params as JsonObject)?["protocolVersion"] is JsonValue value
               && value.TryGetValue(out string? proposed)
            ? proposed
            : null;
    }

    /// <summary>
    ///     True when nothing answered. A JSON-RPC error is an answer, and so is an HTTP status —
    ///     both mean the backend is alive and must not be re-acquired.
    /// </summary>
    private static bool LostTheBackend(Exception ex) =>
        ex switch
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

    /// <summary>
    ///     Holds the session in use, swaps it once per loss however many calls saw that loss, and keeps
    ///     it on the revision the client speaks (docs/adr/0020-always-on-http-stdio-proxy.md).
    /// </summary>
    private sealed class BackendChannel(McpSession backend, IBackendSessions backendSessions)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private McpSession _current = backend;
        private string? _revision;

        private McpSession Current => Volatile.Read(ref _current);

        /// <summary>
        ///     The session to relay on: the current one unless the client asked for a revision it did
        ///     not open at, in which case a session on that revision replaces it.
        /// </summary>
        public async Task<McpSession> SpeakingAsync(string? revision, CancellationToken cancellationToken)
        {
            var current = Current;
            if (Speaks(current, revision))
            {
                return current;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                return Speaks(Current, revision) ? Current : await OpenAsync(revision, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<McpSession> ReplaceAsync(McpSession lost, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                // Concurrent callers all fail on the same dead session; only the first re-acquires,
                // and it re-opens on the revision the client is already speaking.
                return ReferenceEquals(Current, lost) ? await OpenAsync(_revision, cancellationToken) : Current;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<McpSession> OpenAsync(string? revision, CancellationToken cancellationToken)
        {
            var session = await backendSessions.OpenAsync(revision, cancellationToken);
            Volatile.Write(ref _current, session);
            _revision = revision;
            return session;
        }

        /// <summary>A client that names no revision takes whatever session is open; it has no opinion.</summary>
        private static bool Speaks(McpSession session, string? revision) => revision is null || session.NegotiatedProtocolVersion == revision;
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
