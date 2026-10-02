using System.Diagnostics.CodeAnalysis;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Cloud providers return ETags quoted; stores keep and compare them unquoted.</summary>
internal static class CloudEtag
{
    /// <summary>Removes the surrounding quotes from a provider ETag.</summary>
    [return: NotNullIfNotNull(nameof(etag))]
    public static string? Strip(string? etag) => etag?.Trim('"');
}
