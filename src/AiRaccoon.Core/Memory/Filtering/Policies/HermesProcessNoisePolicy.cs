using System;
namespace AiRaccoon.Core.Memory.Filtering.Policies;

public sealed class HermesProcessNoisePolicy : INoiseFilterPolicy
{
    public string Name => "HermesBackgroundProcessLog";
    public NoiseFilterResult Evaluate(MemoryWriteRequest request)
    {
        var content = request.Content.TrimStart();
        if (content.StartsWith("[IMPORTANT: Background process", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("completed normally", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("Command:", StringComparison.OrdinalIgnoreCase))
        {
            return NoiseFilterResult.Noise(Name);
        }
        return NoiseFilterResult.Clean;
    }
}
