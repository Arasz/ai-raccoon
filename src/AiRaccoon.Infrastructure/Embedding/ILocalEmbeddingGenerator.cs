using Microsoft.Extensions.AI;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>An in-process embedding generator that reports where it runs, for the session-created log line and doctor.</summary>
internal interface ILocalEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The execution provider rows run on, with a parenthesized suffix for each fallback taken.</summary>
    string ExecutionProvider { get; }

    /// <summary>ORT intra-op threads the session was built with; 0 means ORT's own default.</summary>
    int IntraOpThreads { get; }
}
