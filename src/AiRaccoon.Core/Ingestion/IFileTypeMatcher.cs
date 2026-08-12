using System.Diagnostics.CodeAnalysis;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
/// Matches file paths or extensions to registered file type handlers.
/// </summary>
public interface IFileTypeMatcher
{
    bool TryGetHandler(string path, [NotNullWhen(true)] out IFileTypeHandler? handler);
    bool IsSupported(string path);
    IReadOnlySet<string> SupportedExtensions { get; }
}
