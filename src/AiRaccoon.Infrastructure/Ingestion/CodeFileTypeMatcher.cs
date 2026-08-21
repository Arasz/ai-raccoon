using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Case-insensitive membership test over <see cref="CodeExtensions" /> — same dot-prefixed,
///     lowercased normalize shape as <see cref="FileTypeMatcher" />. The memory matcher stays
///     untouched; this is a separate registry with no `IFileTypeHandler` indirection (code has one
///     chunking algorithm for every extension, not a handler per extension).
/// </summary>
public sealed class CodeFileTypeMatcher : ICodeFileTypeMatcher
{
    public bool IsCodeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return CodeExtensions.All.Contains(NormalizeExtension(Path.GetExtension(path)));
    }

    private static string NormalizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
        {
            return string.Empty;
        }

        var trimmed = ext.Trim();
        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : "." + trimmed.ToLowerInvariant();
    }
}
