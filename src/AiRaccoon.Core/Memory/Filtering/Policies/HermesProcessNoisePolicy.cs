namespace AiRaccoon.Core.Memory.Filtering.Policies;

/// <summary>
///     Rejects the three machine-notification shapes this harness emits. Environment-specific by
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

    /// <summary>
    ///     The exact-body shape of the third notification: "received signal &lt;digits&gt;". Measured:
    ///     7 rows in the live bank (six "received signal 1", one "received signal 15"), none graded
    ///     above 2/5.
    /// </summary>
    private const string ReceivedSignalPrefix = "received signal";

    public string Name => "HermesBackgroundProcessLog";

    public ValueTask<NoiseFilterResult> EvaluateAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var content = request.Content.Trim();
        foreach (var prefix in NotificationPrefixes)
        {
            if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(NoiseFilterResult.Noise(Name));
            }
        }

        if (IsReceivedSignalNotice(content))
        {
            return ValueTask.FromResult(NoiseFilterResult.Noise(Name));
        }

        return ValueTask.FromResult(NoiseFilterResult.Clean);
    }

    /// <summary>Matches only an exact "received signal &lt;digits&gt;[.]" body — no leading or trailing text.</summary>
    private static bool IsReceivedSignalNotice(string trimmedContent)
    {
        if (!trimmedContent.StartsWith(ReceivedSignalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = trimmedContent[ReceivedSignalPrefix.Length..].Trim().TrimEnd('.');
        if (remainder.Length == 0)
        {
            return false;
        }

        foreach (var character in remainder)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
