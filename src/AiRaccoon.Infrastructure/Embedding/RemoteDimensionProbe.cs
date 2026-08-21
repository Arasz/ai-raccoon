using Microsoft.Extensions.AI;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Asks an OpenAI-compatible endpoint how wide its embeddings actually are (plan D10).</summary>
public interface IRemoteDimensionProbe
{
    /// <summary>One embedding call against the endpoint, returning the vector length it produced.</summary>
    Task<int> ProbeAsync(string model, string? baseUrl, string? apiKey, CancellationToken cancellationToken);
}

/// <summary>
///     Embeds a single throwaway string and measures the result. Used before the migration outbox
///     commits: a declared dimension that the endpoint contradicts has to be refused while nothing
///     has been written, because the drain discovers it too late — the bank is already pending
///     behind a closed ToolGate with no way to finish (ADR-0076).
/// </summary>
public sealed class RemoteDimensionProbe(IEmbeddingService embeddings) : IRemoteDimensionProbe
{
    private const string ProbeText = "probe";

    public async Task<int> ProbeAsync(string model, string? baseUrl, string? apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        using var generator = embeddings.CreateGenerator(new EmbeddingSettings("openai", model, baseUrl, apiKey));
        var result = await generator.GenerateAsync([ProbeText], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result[0].Vector.Length;
    }
}
