using System.Text.Json;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Observability;

/// <summary>
///     The one place a tool call is timed, traced and counted: a CallToolFilter registered inside
///     <see cref="ToolRefusals" /> so it still sees the raw exception (docs/work/2026-08-09-otlp-fix-plan.md D1).
/// </summary>
internal static class ToolTelemetry
{
    /// <summary>Counter project id for a refused call — bounded, so a refused caller cannot mint a series per id it invents.</summary>
    internal const string RefusedProjectId = "refused";

    /// <summary>Counter project id for the one tool that names several projects at once.</summary>
    internal const string MultiProjectId = "multi";

    /// <summary>Span and counter project id for the one tool whose project id is optional.</summary>
    internal const string AllProjectsId = "all";

    private const string ProjectIdArgument = "projectId";
    private const string ProjectIdsArgument = "projectIds";

    /// <summary>
    ///     Tools whose project id is not a plain <c>projectId</c> string. Every other tool takes the
    ///     default projection; <c>ToolTelemetryProjectionTests</c> derives both halves from the
    ///     registered tools so a rename or a new shape cannot pass unnoticed.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Func<IDictionary<string, JsonElement>?, ToolProject>>
        Projections = new Dictionary<string, Func<IDictionary<string, JsonElement>?, ToolProject>>(StringComparer.Ordinal)
        {
            ["memory_promotion_list"] = arguments => new ToolProject(Text(arguments, ProjectIdArgument) ?? AllProjectsId, null),
            ["memory_share_extract"] = arguments => new ToolProject(JoinedText(arguments, ProjectIdsArgument), MultiProjectId)
        };

    /// <summary>The filter: every call to every registered tool, present and future, passes through it.</summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var metrics = request.Services?.GetService<ToolCallMetrics>();
            var recorder = request.Services?.GetService<IMeasurementRecorder>();
            return metrics is null
                ? await next(request, cancellationToken).ConfigureAwait(false)
                : await RecordAsync(metrics, request.Params?.Name ?? string.Empty, request.Params?.Arguments,
                    token => next(request, token), cancellationToken, recorder).ConfigureAwait(false);
        };

    /// <summary>
    ///     Times one invocation and records it once it has returned — success only after the result
    ///     exists, so a failure while producing it counts as an error. <paramref name="recorder" /> is
    ///     the WP8 tool-level measurement hook — best-effort, so a null recorder just skips it.
    /// </summary>
    internal static async ValueTask<CallToolResult> RecordAsync(ToolCallMetrics metrics, string toolName,
        IDictionary<string, JsonElement>? arguments,
        Func<CancellationToken, ValueTask<CallToolResult>> next, CancellationToken cancellationToken,
        IMeasurementRecorder? recorder = null)
    {
        var project = ProjectFor(toolName, arguments);
        using var activity = new ToolExecutionActivity(metrics, toolName, project.SpanId, project.MetricId, recorder);
        try
        {
            var result = await next(cancellationToken).ConfigureAwait(false);
            activity.RecordInvocation();
            return result;
        }
        catch (Exception exception)
        {
            activity.RecordError(exception, Refused(exception) ? RefusedProjectId : null);
            throw;
        }
    }

    /// <summary>The project ids this call names: the span keeps what the caller sent, the counter keeps a bounded value.</summary>
    internal static ToolProject ProjectFor(string toolName, IDictionary<string, JsonElement>? arguments) =>
        Projections.TryGetValue(toolName, out var projection)
            ? projection(arguments)
            : new ToolProject(Text(arguments, ProjectIdArgument) ?? string.Empty, null);

    /// <summary>A refusal is what <see cref="ToolRefusals" /> already calls one — no second list of what counts.</summary>
    private static bool Refused(Exception exception) => ToolRefusals.PrefixFor(exception) is not null || exception is McpException;

    private static string? Text(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments?.TryGetValue(name, out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string JoinedText(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments?.TryGetValue(name, out var value) == true && value.ValueKind == JsonValueKind.Array
            ? string.Join(",", value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()))
            : string.Empty;

    /// <summary>SpanId is what the caller sent; MetricId is the bounded value, or null to reuse SpanId.</summary>
    internal readonly record struct ToolProject(string SpanId, string? MetricId);
}
