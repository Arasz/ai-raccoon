using System.Text.Json;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Sync;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using FluentValidation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>
///     Turns an expected refusal into a normal error <see cref="CallToolResult" /> instead of an
///     escaping exception: the SDK's <c>McpServerImpl</c> logs Error on every exception a tool call
///     throws, <see cref="McpException" /> included. Registered once as a CallToolFilter in McpServerSetup.ConfigureMcpTransport.
/// </summary>
internal static partial class ToolRefusals
{
    /// <summary>The one table both <see cref="PrefixFor" /> and its tests read — no second hand-kept copy.</summary>
    internal static readonly IReadOnlyDictionary<Type, string> RefusalPrefixes = new Dictionary<Type, string>
    {
        [typeof(PathOutsideScopeException)] = "path-outside-scope",
        [typeof(PathNotFoundException)] = "path-not-found",
        [typeof(UnknownWorkspaceException)] = "unknown-workspace",
        [typeof(UnknownHashException)] = "unknown-hash",
        [typeof(UnsupportedSchemaVersionException)] = "schema-version-unsupported",
        [typeof(WatchDisabledException)] = "watching-disabled",
        [typeof(SyncNotConfiguredException)] = "sync-not-configured",
        [typeof(SyncAuthFailedException)] = "sync-auth-failed",
        [typeof(SyncConflictException)] = "sync-conflict",
        [typeof(SyncNetworkException)] = "sync-network",
        [typeof(SyncCorruptFileException)] = "sync-corrupt-file",
        [typeof(AccessDeniedException)] = "access-denied",
        // ADR-0076: refuses every bank operation for the duration of a model migration.
        [typeof(ModelMigrationInProgressException)] = "model-migration-in-progress",
        [typeof(ContextOutsideProjectException)] = "context-outside-project",
        // The install this process started from was replaced/removed under it (e.g. 'dotnet tool
        // update'); a plain InvalidOperationException still means the asset is genuinely missing
        // and stays unmapped (docs/reference/agent-memory-server.md Error shapes).
        [typeof(BundledModelInstallReplacedException)] = "embedding-install-replaced",
        // Moved out of DirectThrowPrefixes by docs/adr/0065: the consent gate now lives in
        // ShareExtractService, so it raises a domain exception instead of a bare McpException.
        [typeof(ConfirmationRequiredException)] = "confirm-required",
        [typeof(ValidationException)] = "invalid-params",
        // The SDK's own argument marshaller (Microsoft.Extensions.AI.AIFunctionFactory) throws a
        // raw JsonException when a call's JSON shape doesn't match a parameter's declared type,
        // and a plain ArgumentException when a required parameter is missing; our guard clauses
        // throw ArgumentException/ArgumentNullException on a present-but-blank or null value.
        // ArgumentOutOfRangeException is deliberately absent — see PrefixFor.
        [typeof(JsonException)] = "invalid-argument",
        [typeof(ArgumentException)] = "invalid-argument",
        [typeof(ArgumentNullException)] = "invalid-argument"
    };

    /// <summary>
    ///     Wire prefixes thrown directly as a bare <see cref="McpException" /> message rather than
    ///     mapped from an exception type here (ToolGate.cs, MemoryTools.cs, ShareTools.cs,
    ///     PromotionTools.cs). Kept next to <see cref="RefusalPrefixes" /> so the doc-drift test's expected set stays code-derived.
    /// </summary>
    internal static readonly IReadOnlyCollection<string> DirectThrowPrefixes = ["invalid-params"];

    /// <summary>Expected refusals remain visible at Warning without being logged as errors.</summary>
    private static readonly HashSet<string> WarningPrefixes =
        ["sync-network", "sync-corrupt-file", "unknown-hash", "embedding-install-replaced"];

    /// <summary>
    ///     The wire prefix for a known refusal, or null when the exception is a genuine failure.
    ///     Matched on the exact type: ArgumentOutOfRangeException is how .NET reports our own index
    ///     arithmetic going wrong, so laundering it into "invalid-argument" would both mute
    ///     Error-level alerting and tell the caller to retry arguments that were never at fault.
    /// </summary>
    internal static string? PrefixFor(Exception exception) => RefusalPrefixes.GetValueOrDefault(exception.GetType());

    /// <summary>The log level for a refused prefix: <see cref="WarningPrefixes" /> log at Warning, everything else at Information.</summary>
    internal static LogLevel LevelFor(string prefix) => WarningPrefixes.Contains(prefix) ? LogLevel.Warning : LogLevel.Information;

    /// <summary>
    ///     The CallToolFilter: a protocol exception or cancellation always rethrows; a mapped refusal
    ///     or a bare <see cref="McpException" /> (already client-facing text) becomes an error result
    ///     instead; anything else rethrows and stays fail-level.
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
                return Refused(request, $"{prefix}: {ex.Message}", prefix, LevelFor(prefix));
            }
            catch (McpException ex)
            {
                // Every prefix thrown directly as a bare McpException (invalid-params,
                // confirm-required) is a user-input mistake, never an infrastructure fault.
                return Refused(request, ex.Message, "refused", LogLevel.Information);
            }
            catch (Exception ex)
            {
                // Nothing maps this, so it is a genuine failure — but letting it escape cost the
                // caller every clue: the SDK replaces it with "An error occurred invoking '<tool>'."
                // (docs/adr/0061). Answering here keeps the Error record the SDK used to produce,
                // which is why Unexpected logs at Error itself rather than relying on the rethrow.
                return Unexpected(request, ex);
            }
        };

    /// <summary>
    ///     What the caller is told about an exception nothing maps: the type, and nothing else. The
    ///     message stays server-side — a refusal's text is chosen for the caller, an unexpected
    ///     failure's is not, and may carry a bank path or a SQL fragment (docs/adr/0061).
    /// </summary>
    internal static string UnexpectedText(Exception exception) =>
        $"unexpected-error: {exception.GetType().Name}";

    /// <summary>
    ///     Answers an unmapped exception instead of letting it escape, and logs it at Error itself:
    ///     the SDK produced that Error record only because the exception reached it, so catching
    ///     here without logging would trade eleven useless words for silence.
    /// </summary>
    private static CallToolResult Unexpected(RequestContext<CallToolRequestParams> request, Exception exception)
    {
        var logger = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("AiRaccoon.Tools.ToolRefusals");
        if (logger is not null)
        {
            Log.ToolFailed(logger, request.Params?.Name ?? string.Empty, exception.GetType().Name, exception);
        }

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = UnexpectedText(exception) }]
        };
    }

    private static CallToolResult Refused(RequestContext<CallToolRequestParams> request, string message,
        string prefix, LogLevel level)
    {
        var logger = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("AiRaccoon.Tools.ToolRefusals");
        if (logger is not null)
        {
            if (level == LogLevel.Warning)
            {
                Log.ToolRefusedAtWarning(logger, request.Params?.Name ?? string.Empty, prefix);
            }
            else
            {
                Log.ToolRefused(logger, request.Params?.Name ?? string.Empty, prefix);
            }
        }

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] };
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 910, Level = LogLevel.Information, Message = "\"{ToolName}\" refused: {Reason}")]
        public static partial void ToolRefused(ILogger logger, string toolName, string reason);

        [LoggerMessage(EventId = 911, Level = LogLevel.Warning, Message = "\"{ToolName}\" refused: {Reason}")]
        public static partial void ToolRefusedAtWarning(ILogger logger, string toolName, string reason);

        [LoggerMessage(EventId = 912, Level = LogLevel.Error, Message = "\"{ToolName}\" failed with an unmapped {ExceptionType}")]
        public static partial void ToolFailed(ILogger logger, string toolName, string exceptionType, Exception exception);
    }
}
