using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Sync;
using FluentValidation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>
///     Turns an expected refusal into a normal error <see cref="CallToolResult" /> instead of an
///     escaping exception (#151): the SDK's <c>McpServerImpl</c> logs Error on every exception a
///     tool call throws, <see cref="McpException" /> included, so a correct refusal used to log as
///     a crash. Registered once as a CallToolFilter in McpServerSetup.ConfigureMcpTransport.
/// </summary>
internal static partial class ToolRefusals
{
    /// <summary>The one table both <see cref="PrefixFor" /> and its tests read — no second hand-kept copy.</summary>
    internal static readonly IReadOnlyDictionary<Type, string> RefusalPrefixes = new Dictionary<Type, string>
    {
        [typeof(PathOutsideScopeException)] = "path-outside-scope",
        [typeof(PathNotFound)] = "path-not-found",
        [typeof(UnknownWorkspaceException)] = "unknown-workspace",
        [typeof(WatchDisabledException)] = "watching-disabled",
        [typeof(SyncNotConfiguredException)] = "sync-not-configured",
        [typeof(SyncAuthFailedException)] = "sync-auth-failed",
        [typeof(SyncConflictException)] = "sync-conflict",
        [typeof(SyncNetworkException)] = "sync-network",
        [typeof(SyncCorruptFileException)] = "sync-corrupt-file",
        [typeof(AccessDeniedException)] = "access-denied",
        [typeof(ValidationException)] = "invalid-params"
    };

    /// <summary>
    ///     Wire prefixes thrown directly as a bare <see cref="McpException" /> message rather than
    ///     mapped from an exception type here — throw sites: ToolGate.cs ("invalid-params"),
    ///     MemoryTools.cs ("invalid-params"), ShareTools.cs ("invalid-params", "confirm-required"),
    ///     PromotionTools.cs ("invalid-params"). Kept next to <see cref="RefusalPrefixes" /> so the
    ///     doc-drift test's expected set stays code-derived rather than hand-duplicated.
    /// </summary>
    internal static readonly IReadOnlyCollection<string> DirectThrowPrefixes = ["invalid-params", "confirm-required"];

    /// <summary>The wire prefix for a known refusal, or null when the exception is a genuine failure.</summary>
    internal static string? PrefixFor(Exception exception) => RefusalPrefixes.GetValueOrDefault(exception.GetType());

    /// <summary>
    ///     The CallToolFilter: a protocol exception or a cancellation of the request token always
    ///     rethrows; a mapped refusal or a bare <see cref="McpException" /> (whose message is already
    ///     the intended client-facing text) becomes an error result instead; anything else rethrows
    ///     and stays fail-level.
    /// </summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (McpProtocolException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (PrefixFor(ex) is { } prefix)
            {
                return Refused(request, $"{prefix}: {ex.Message}", ex);
            }
            catch (McpException ex)
            {
                return Refused(request, ex.Message, ex);
            }
        };

    private static CallToolResult Refused(RequestContext<CallToolRequestParams> request, string message,
        Exception exception)
    {
        var logger = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("AiRaccoon.Tools.ToolRefusals");
        if (logger is not null)
        {
            Log.ToolRefused(logger, request.Params?.Name ?? string.Empty, message, exception);
        }

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] };
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 910, Level = LogLevel.Information, Message = "\"{ToolName}\" refused: {Reason}")]
        public static partial void ToolRefused(ILogger logger, string toolName, string reason, Exception exception);
    }
}
