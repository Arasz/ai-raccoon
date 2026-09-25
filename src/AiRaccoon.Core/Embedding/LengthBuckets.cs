namespace AiRaccoon.Core.Embedding;

/// <summary>
///     Length buckets for engines that compile per input shape (ADR-0114): a row pads up to the next
///     multiple of <see cref="Step" /> tokens, never past the model's window.
/// </summary>
public static class LengthBuckets
{
    /// <summary>Bucket width in tokens. Every shipped chunk budget plus [CLS]/[SEP] is a multiple of it.</summary>
    public const int Step = 64;

    /// <summary>CoreML's bucket width in tokens (ADR-0118): coarser than <see cref="Step" /> because finer buckets cost more cache than they save.</summary>
    public const int CoreMlStep = 256;

    /// <summary>CoreML's largest bucket in tokens (ADR-0118). The generator routes longer rows to a CPU fallback session instead of calling this method.</summary>
    public const int CoreMlWindow = 1024;

    /// <summary>The padded sequence length for a row of <paramref name="length" /> tokens, at the default <see cref="Step" />.</summary>
    public static int PaddedLength(int length, int window) => PaddedLength(length, window, Step);

    /// <summary>The padded sequence length for a row of <paramref name="length" /> tokens, rounded up to the next multiple of <paramref name="step" />.</summary>
    public static int PaddedLength(int length, int window, int step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, length);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        return Math.Min(window, (length + step - 1) / step * step);
    }
}
