using System.Diagnostics.CodeAnalysis;

namespace AiRaccoon.Core.Projects;

/// <summary>
///     ADR-0089 decision 2: canonicalizes any <see cref="Guid.TryParse(string, out Guid)" />-accepted
///     spelling to the lowercase D-form (<c>8-4-4-4-12</c>); a non-guid id passes through untouched.
/// </summary>
public static class ProjectId
{
    /// <summary>Canonicalizes a guid spelling; returns a non-guid id unchanged.</summary>
    public static string Canonicalize(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return TryCanonicalize(projectId, out var canonical) ? canonical : projectId;
    }

    /// <summary>
    ///     True and the lowercase D-form when <paramref name="projectId" /> parses as a guid; false and
    ///     the input unchanged otherwise. Never throws — <see cref="Canonicalize" /> guards blank input.
    /// </summary>
    public static bool TryCanonicalize(string? projectId, [NotNullWhen(true)] out string? canonical)
    {
        if (!string.IsNullOrWhiteSpace(projectId) && Guid.TryParse(projectId, out var guid))
        {
            canonical = guid.ToString("D");
            return true;
        }

        canonical = projectId;
        return false;
    }
}
