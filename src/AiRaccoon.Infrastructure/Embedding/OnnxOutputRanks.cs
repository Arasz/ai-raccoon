using Microsoft.ML.OnnxRuntime;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The one rank question every path asks a graph (#466, #470): the manifest's pooling.mode is
///     an inference, an output's declared rank is a fact — rank 3 is token-level, rank 2 is a
///     vector the graph pooled itself. The embed path, the download path and the manifest repair
///     share this instead of each writing the check again.
/// </summary>
internal static class OnnxOutputRanks
{
    /// <summary>Rank of an output the graph already pooled: <c>[batch, dimensions]</c>.</summary>
    public const int PooledRank = 2;

    /// <summary>Rank of a genuine token-level output: <c>[batch, sequence, dimensions]</c>.</summary>
    public const int TokenLevelRank = 3;

    extension(InferenceSession session)
    {
        /// <summary>Rank the graph declares for one output; 0 when the graph has no such output.</summary>
        public int OutputRank(string outputName) =>
            session.OutputMetadata.TryGetValue(outputName, out var metadata) ? metadata.Dimensions?.Length ?? 0 : 0;

        /// <summary>Every output's declared rank, keyed by the name the graph gives it.</summary>
        public IReadOnlyDictionary<string, int> OutputRanks() =>
            session.OutputMetadata.ToDictionary(o => o.Key, o => o.Value.Dimensions?.Length ?? 0, StringComparer.Ordinal);
    }
}
