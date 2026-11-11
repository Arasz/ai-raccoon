using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Decisions;

namespace AiRaccoon.Decisions.Client;

/// <summary>
///     The CLI's view of the server's decision routes. Each call returns the whole response envelope,
///     fully read: a body that ends early throws instead of returning a partial document.
/// </summary>
internal interface IDecisionClient
{
    Task<JsonDocument> RegisterTaskAsync(string projectId, RegisterTaskRequest request, CancellationToken cancellationToken = default);

    /// <summary>The complete export; it runs without the per-request deadline because its size is unbounded.</summary>
    Task<JsonDocument> ExportAsync(string projectId, DecisionTaskSelector? selector, CancellationToken cancellationToken = default);

    Task<JsonDocument> PreviewAsync(string projectId, DecisionPreviewOptions options, CancellationToken cancellationToken = default);

    /// <summary>Records a decision from <paramref name="body" />, the record request as the server binds it.</summary>
    Task<JsonDocument> RecordAsync(string projectId, JsonObject body, CancellationToken cancellationToken = default);

    Task<JsonDocument> GetAsync(string projectId, string decisionId, bool includeHistory, CancellationToken cancellationToken = default);

    Task<JsonDocument> QueryAsync(string projectId, DecisionQuery query, CancellationToken cancellationToken = default);

    /// <summary>The dry run of a Semantica import; it runs without the per-request deadline because the files are large.</summary>
    Task<JsonDocument> ImportPreviewAsync(string projectId, byte[] source, byte[] mapping, CancellationToken cancellationToken = default);

    /// <summary>Applies a previewed Semantica import; it runs without the per-request deadline because the files are large.</summary>
    Task<JsonDocument> ImportApplyAsync(string projectId, ImportApplyRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The server refused a decision request; <see cref="Kind" /> is the refusal kind it reported.</summary>
internal sealed class DecisionRefusedException(string kind, int status, string message) : Exception(message)
{
    public string Kind { get; } = kind;

    public int Status { get; } = status;
}

/// <summary>The graph that writes the bank directly (`encryption`) never reaches the server; decision verbs are not dispatched there.</summary>
internal sealed class DirectGraphDecisionClient : IDecisionClient
{
    public Task<JsonDocument> RegisterTaskAsync(string projectId, RegisterTaskRequest request, CancellationToken cancellationToken = default) => throw Refused();

    public Task<JsonDocument> ExportAsync(string projectId, DecisionTaskSelector? selector, CancellationToken cancellationToken = default) => throw Refused();

    public Task<JsonDocument> PreviewAsync(string projectId, DecisionPreviewOptions options, CancellationToken cancellationToken = default) => throw Refused();

    public Task<JsonDocument> RecordAsync(string projectId, JsonObject body, CancellationToken cancellationToken = default) => throw Refused();

    public Task<JsonDocument> GetAsync(string projectId, string decisionId, bool includeHistory, CancellationToken cancellationToken = default) => throw Refused();

    public Task<JsonDocument> QueryAsync(string projectId, DecisionQuery query, CancellationToken cancellationToken = default) => throw Refused();

    public Task<JsonDocument> ImportPreviewAsync(string projectId, byte[] source, byte[] mapping, CancellationToken cancellationToken = default) => throw Refused();

    public Task<JsonDocument> ImportApplyAsync(string projectId, ImportApplyRequest request, CancellationToken cancellationToken = default) => throw Refused();

    private static NotSupportedException Refused() => new("ai-raccoon: decisions are read and written only through the server; this command writes the bank directly");
}
