using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Decisions;
using AiRaccoon.Decisions.Client;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     An <see cref="IDecisionClient" /> that records each call by operation name and answers it through
///     <see cref="Respond" />; by default an envelope every decisions verb can print.
/// </summary>
internal sealed class FakeDecisionClient : IDecisionClient
{
    /// <summary>An envelope whose value carries a preview text, so JSON and text output both render.</summary>
    public const string Envelope = """{"data":{"projectId":"p","projectRevision":1,"value":{"text":"preview"}}}""";

    public List<string> Calls { get; } = [];

    public Func<string, CancellationToken, Task<JsonDocument>> Respond { get; set; } =
        (_, _) => Task.FromResult(JsonDocument.Parse(Envelope));

    public Task<JsonDocument> RegisterTaskAsync(string projectId, RegisterTaskRequest request, CancellationToken cancellationToken = default) =>
        Call("tasks", cancellationToken);

    public Task<JsonDocument> ExportAsync(string projectId, DecisionTaskSelector? selector, CancellationToken cancellationToken = default) =>
        Call("export", cancellationToken);

    public Task<JsonDocument> PreviewAsync(string projectId, DecisionPreviewOptions options, CancellationToken cancellationToken = default) =>
        Call("preview", cancellationToken);

    public Task<JsonDocument> RecordAsync(string projectId, JsonObject body, CancellationToken cancellationToken = default) =>
        Call("records", cancellationToken);

    public Task<JsonDocument> GetAsync(string projectId, string decisionId, bool includeHistory, CancellationToken cancellationToken = default) =>
        Call("get", cancellationToken);

    public Task<JsonDocument> QueryAsync(string projectId, DecisionQuery query, CancellationToken cancellationToken = default) =>
        Call("query", cancellationToken);

    private Task<JsonDocument> Call(string operation, CancellationToken cancellationToken)
    {
        Calls.Add(operation);
        return Respond(operation, cancellationToken);
    }
}
