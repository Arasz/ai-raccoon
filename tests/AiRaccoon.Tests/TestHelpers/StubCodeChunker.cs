using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Groups consecutive non-blank lines into one chunk, with real 1-based line ranges computed
///     from the actual line positions — a plumbing-level stand-in for the engine-blocked
///     `CodeChunker` (WP2), not a budget/brace-balance implementation.
/// </summary>
public sealed class StubCodeChunker : ICodeChunker
{
    public IReadOnlyList<CodeChunk> Chunk(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var chunks = new List<CodeChunk>();
        int? start = null;
        var buffer = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                Flush(chunks, buffer, ref start, lineNumber - 1);
                continue;
            }

            start ??= lineNumber;
            buffer.Add(lines[i]);
        }

        Flush(chunks, buffer, ref start, lines.Length);
        return chunks;
    }

    private static void Flush(List<CodeChunk> chunks, List<string> buffer, ref int? start, int lineEnd)
    {
        if (start is null)
        {
            return;
        }

        chunks.Add(new CodeChunk(string.Join('\n', buffer), start.Value, lineEnd));
        buffer.Clear();
        start = null;
    }
}
