using System.Text.Json;
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

    private static NotSupportedException Refused() =>
        new("ai-raccoon: decisions are read and written only through the server; this command writes the bank directly");
}
