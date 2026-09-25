namespace AiRaccoon.Core.Embedding;

/// <summary>
///     Length buckets for engines that compile per input shape (ADR-0113): a row pads up to the next
///     multiple of <see cref="Step" /> tokens, never past the model's window.
/// </summary>
public static class LengthBuckets
{
    /// <summary>Bucket width in tokens. Every shipped chunk budget plus [CLS]/[SEP] is a multiple of it.</summary>
    public const int Step = 64;

    /// <summary>The padded sequence length for a row of <paramref name="length" /> tokens.</summary>
    public static int PaddedLength(int length, int window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, length);
        return Math.Min(window, (length + Step - 1) / Step * Step);
    }
}
