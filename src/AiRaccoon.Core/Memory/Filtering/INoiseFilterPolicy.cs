namespace AiRaccoon.Core.Memory.Filtering;

public readonly record struct NoiseFilterResult(bool IsNoise, string? PolicyName = null)
{
    public static NoiseFilterResult Clean => new(false);
    public static NoiseFilterResult Noise(string policyName) => new(true, policyName);
}

public interface INoiseFilterPolicy
{
    string Name { get; }
    NoiseFilterResult Evaluate(MemoryWriteRequest request);
}
