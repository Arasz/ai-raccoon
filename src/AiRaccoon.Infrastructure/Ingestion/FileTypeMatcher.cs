using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using AiRaccoon.Core.Ingestion;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Immutable matcher linking file extensions to registered <see cref="IFileTypeHandler" /> instances.
/// </summary>
public sealed class FileTypeMatcher : IFileTypeMatcher
{
    private readonly FrozenDictionary<string, IFileTypeHandler> _handlersByExtension;

    public FileTypeMatcher(IReadOnlyCollection<IFileTypeHandler> handlers)
    {
        Guard.IsNotNull(handlers);

        var map = new Dictionary<string, IFileTypeHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            foreach (var ext in handler.Extensions)
            {
                var normalized = NormalizeExtension(ext);
                if (!map.TryAdd(normalized, handler))
                {
                    throw new InvalidOperationException($"Duplicate extension '{normalized}' registered across file type handlers ('{map[normalized].Name}' and '{handler.Name}').");
                }
            }
        }

        _handlersByExtension = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetHandler(string path, [NotNullWhen(true)] out IFileTypeHandler? handler)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            handler = null;
            return false;
        }

        var ext = NormalizeExtension(Path.GetExtension(path));
        return _handlersByExtension.TryGetValue(ext, out handler);
    }

    public bool IsSupported(string path) => TryGetHandler(path, out _);

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
