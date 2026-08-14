using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Memory.Filtering.Policies;

/// <summary>
///     Rejects the two machine-notification shapes this harness emits. Environment-specific by
///     design — a general detector is a separate, unresolved question (ADR-0039).
/// </summary>
public sealed class HermesProcessNoisePolicy : INoiseFilterPolicy
{
    /// <summary>
    ///     The prefixes that mark a notification. Measured read-only against the live bank's 399
    ///     search_quality rows: 29 rows carry the first, 25 the second, and every graded one of
    ///     them scored 2/5 — never higher.
    /// </summary>
    private static readonly string[] NotificationPrefixes =
    [
        "[IMPORTANT: Background process",
        "[ASYNC DELEGATION BATCH"
    ];

    public string Name => "HermesBackgroundProcessLog";

    public ValueTask<NoiseFilterResult> EvaluateAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = request.Content.TrimStart();
        foreach (var prefix in NotificationPrefixes)
        {
            if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(NoiseFilterResult.Noise(Name));
            }
        }

        return ValueTask.FromResult(NoiseFilterResult.Clean);
    }
}
