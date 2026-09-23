namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Detects binary content read as text: a NUL in the first <see cref="InspectedLength" />
///     characters, the heuristic git uses. Ingest skips such a file as if it were empty.
/// </summary>
public static class BinaryContent
{
    public const int InspectedLength = 8000;

    public static bool IsBinary(string content) =>
        content.AsSpan(0, Math.Min(content.Length, InspectedLength)).Contains('\0');
}
