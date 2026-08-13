namespace AiRaccoon.Core.Memory.Filtering;

using System.Threading;
using System.Threading.Tasks;

public readonly record struct NoiseFilterResult(bool IsNoise, string? PolicyName = null)
{
    public static NoiseFilterResult Clean => new(false);
    public static NoiseFilterResult Noise(string policyName) => new(true, policyName);
}

public interface INoiseFilterPolicy
{
    string Name { get; }
    ValueTask<NoiseFilterResult> EvaluateAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
}
