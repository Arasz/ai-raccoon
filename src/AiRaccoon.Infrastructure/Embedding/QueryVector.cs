namespace AiRaccoon.Infrastructure.Embedding;

public sealed record QueryVector(byte[] Data)
{
    public static readonly QueryVector Empty = new([]);
    public bool IsEmpty => Data.Length == 0;

    public double Alpha { get; init; }

    /// <summary>True when the source query exceeded the embedding engine's window and was trimmed before this vector was produced (code corpus only, WP5 — memory's own trim never sets this).</summary>
    public bool Trimmed { get; init; }

    /// <summary>The engine's own relevance floor for the cosines this vector produces (ADR-0108); null keeps the default.</summary>
    public double? RelevanceFloor { get; init; }
}
