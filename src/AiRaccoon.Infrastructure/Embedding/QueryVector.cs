namespace AiRaccoon.Infrastructure.Embedding;

public sealed record QueryVector(byte[] Data)
{
    public static readonly QueryVector Empty = new([]);
    public bool IsEmpty => Data.Length == 0;

    public double Alpha { get; init; }

    /// <summary>True when the source query exceeded the embedding engine's window and was trimmed before this vector was produced (code corpus only, WP5 — memory's own trim never sets this).</summary>
    public bool Trimmed { get; init; }
}
