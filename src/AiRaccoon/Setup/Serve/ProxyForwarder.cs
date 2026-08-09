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
    ///     Relays requests and notifications to <paramref name="backend" />, suppressing the local
    ///     handlers; any other message kind falls through to them.
    /// </summary>
    internal static McpMessageFilter Create(McpSession backend, ILogger logger)
    {
        Guard.IsNotNull(backend);
        Guard.IsNotNull(logger);
        return next => async (context, cancellationToken) =>
        {
            switch (context.JsonRpcMessage)
            {
                case JsonRpcRequest request:
                    await RelayAsync(backend, logger, context, request, cancellationToken);
                    return;
                case JsonRpcNotification notification:
                    await RelayAsync(backend, logger, notification, cancellationToken);
                    return;
                default:
                    await next(context, cancellationToken);
                    return;
            }
        };
    }

    private static async Task RelayAsync(McpSession backend, ILogger logger, MessageContext context,
        JsonRpcRequest request, CancellationToken cancellationToken)
    {
        Log.RequestRelayed(logger, request.Method);
        JsonRpcResponse answer;
        try
        {
            // A fresh message: the incoming one carries this session's transport, which would send
            // the relayed copy straight back to the client instead of to the backend.
            answer = await backend.SendRequestAsync(
                new JsonRpcRequest { Id = request.Id, Method = request.Method, Params = request.Params },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            // Without this the client is told only "An error occurred.": the SDK collapses every
            // non-MCP exception to that, and the backend's own text is the whole diagnostic.
            Log.RelayFailed(logger, request.Method, ex);
            throw new McpProtocolException($"backend relay failed: {ex.Message}", ex, McpErrorCode.InternalError);
        }

        // The client correlates on the id it chose, so restore it rather than trusting the backend's
        // echo. context.Server is bound to the transport the request arrived on and rejects a
        // caller-supplied context, so the answer carries none.
        await context.Server.SendMessageAsync(
            new JsonRpcResponse { Id = request.Id, Result = answer.Result }, cancellationToken);
    }

    private static async Task RelayAsync(McpSession backend, ILogger logger, JsonRpcNotification notification,
        CancellationToken cancellationToken)
    {
        Log.NotificationRelayed(logger, notification.Method);
        try
        {
            await backend.SendMessageAsync(
                new JsonRpcNotification { Method = notification.Method, Params = notification.Params },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            Log.RelayFailed(logger, notification.Method, ex);
            throw;
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
    }
}
