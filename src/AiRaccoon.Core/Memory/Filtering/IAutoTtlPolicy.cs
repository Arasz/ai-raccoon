namespace AiRaccoon.Core.Memory.Filtering;

public interface IAutoTtlPolicy
{
    string Name { get; }
    int? EvaluateTtl(MemoryWriteRequest request);
}
