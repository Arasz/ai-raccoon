using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Projects;

/// <summary>
///     Default projectId resolution from the working directory: a tool call that names no project
///     resolves to the single project whose ingest scope (settings rows under "ingest.scope.")
///     or live watch registration contains the server's cwd. Both surfaces are enumerated and
///     merged on EVERY call — never cached at singleton construction, because scopes are edited
///     and watches are (un)registered while the server runs. The union is deduped by CANONICAL
///     project id, so the same guid stored under two spellings (a braced scope row and a D-form
///     watch row) is one project, not a false ambiguity; the first-seen stored spelling is what
///     the resolution carries, and the gate canonicalizes it exactly once anyway. One distinct
///     id resolves, several are refused as ambiguous (never guess), none is None.
///     The legacy "watch.scope." prefix is excluded naturally by the settings enumeration, and
///     "ingest.scope.global" is skipped explicitly (a machine-wide allowlist elects no project).
///     Stored ids travel verbatim — <see cref="Core.Projects.ProjectId.Canonicalize" /> runs once
///     in the gate, never here.
/// </summary>
public sealed class CwdProjectIdResolver(
    ISettingsStore settings,
    IWatchRegisteredStore watches,
    ICwdProbe cwdProbe) : IProjectIdResolver
{
    private static readonly string ScopePrefix = IngestScopeKeys.ScopeProject(string.Empty);

    public async Task<ProjectIdResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var cwd = cwdProbe.CurrentDirectory;
        Dictionary<string, string> candidates = new(StringComparer.Ordinal);

        var scopes = await settings.GetSettingsByPrefixAsync(ScopePrefix, cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in scopes)
        {
            if (key == IngestScopeKeys.ScopeGlobal)
            {
                continue;
            }

            // Malformed stored values are skipped, never fatal — a broken row must not take the
            // whole default-resolution surface down.
            var paths = IngestScopeKeys.Parse(value);
            if (paths is null)
            {
                continue;
            }

            if (paths.Any(path => IngestPath.IsWithinScope(cwd, path)))
            {
                AddCandidate(candidates, key[ScopePrefix.Length..]);
            }
        }

        foreach (var watch in await watches.ListWatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IngestPath.IsWithinScope(cwd, watch.Path))
            {
                AddCandidate(candidates, watch.ProjectId);
            }
        }

        return candidates.Count switch
        {
            0 => new ProjectIdResolution.None(),
            1 => new ProjectIdResolution.Resolved(candidates.Single().Value),
            _ => new ProjectIdResolution.Ambiguous([.. candidates.Values.Order(StringComparer.Ordinal)]),
        };
    }

    /// <summary>
    ///     Dedups on the canonical form of the stored id (guid spellings collapse to the D-form;
    ///     non-guids key on themselves) while keeping the first-seen stored spelling for the
    ///     resolution. The gate canonicalizes the carried spelling exactly once downstream.
    /// </summary>
    private static void AddCandidate(Dictionary<string, string> candidates, string stored)
    {
        var key = ProjectId.TryCanonicalize(stored, out var canonical) ? canonical : stored;
        candidates.TryAdd(key, stored);
    }
}
