using System.Text.Json;
using AiRaccoon.Observability;
using ModelContextProtocol.Protocol;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Runs a tool method the way the server runs it — inside the telemetry filter — so a test can
///     keep injecting fakes into the tool while asserting on what the filter recorded.
/// </summary>
internal static class ToolCallRecorder
{
    public static async Task ThroughFilterAsync(ToolCallMetrics metrics, string toolName,
        IDictionary<string, JsonElement>? arguments, Func<CancellationToken, Task> call,
        CancellationToken cancellationToken) =>
        await ToolTelemetry.RecordAsync(metrics, toolName, arguments, async token =>
        {
            await call(token);
            return new CallToolResult();
        }, cancellationToken);

    /// <summary>The arguments as they arrive on the wire: JSON, not typed method parameters.</summary>
    public static Dictionary<string, JsonElement> Arguments(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value));
}
