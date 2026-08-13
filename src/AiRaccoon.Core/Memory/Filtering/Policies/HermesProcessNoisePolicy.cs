using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Memory.Filtering.Policies;

public sealed class HermesProcessNoisePolicy : INoiseFilterPolicy
{
    public string Name => "HermesBackgroundProcessLog";

    public ValueTask<NoiseFilterResult> EvaluateAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = request.Content.TrimStart();
        if (content.StartsWith("[IMPORTANT: Background process", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("completed normally", StringComparison.OrdinalIgnoreCase) &&
            content.Contains("Command:", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(NoiseFilterResult.Noise(Name));
        }

        return ValueTask.FromResult(NoiseFilterResult.Clean);
    }
}
