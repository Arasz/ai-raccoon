using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>Classes that compile or run the Neural Engine sessions run one at a time: concurrent ANE compiles slow each other past their deadlines.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NeuralEngineCollection
{
    public const string Name = "neural-engine";
}
