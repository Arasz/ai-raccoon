namespace AiRaccoon.Infrastructure.Embedding;

public sealed record QueryVector(byte[] Data)
{
    public static readonly QueryVector Empty = new([]);
    public bool IsEmpty => Data.Length == 0;

    public double Alpha { get; init; }
}
