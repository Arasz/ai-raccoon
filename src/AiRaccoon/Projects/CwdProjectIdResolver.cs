using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Projects;

/// <summary>
///     Default projectId resolution from the working directory: a tool call that names no project
///     resolves to the single project whose ingest scope (settings rows under "ingest.scope.")
///     or live watch registration contains the server's cwd. Both surfaces are enumerated and
///     merged on EVERY call — never cached at singleton construction, because scopes are edited
///     and watches are (un)registered while the server runs. The union is deduped by project id;
///     one distinct id resolves, several are refused as ambiguous (never guess), none is None.
///     The legacy "watch.scope." prefix is excluded naturally by the settings enumeration, and
///     "ingest.scope.global" is skipped explicitly (a machine-wide allowlist elects no project).
///     Stored ids travel verbatim — <see cref="Core.Projects.ProjectId.Canonicalize" /> runs once
///     in the gate, never here.
/// </summary>
public sealed class CwdProjectIdResolver(
    ISettingsStore settings,
    IWatchRegisteredStore watches,
    Func<string>? cwdProbe = null) : IProjectIdResolver
{
    private static readonly string ScopePrefix = IngestScopeKeys.ScopeProject(string.Empty);

    private readonly Func<string> _cwdProbe = cwdProbe ?? (() => Environment.CurrentDirectory);

    public async Task<ProjectIdResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var cwd = _cwdProbe();
        HashSet<string> candidates = new(StringComparer.Ordinal);

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
                candidates.Add(key[ScopePrefix.Length..]);
            }
        }

        foreach (var watch in await watches.ListWatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IngestPath.IsWithinScope(cwd, watch.Path))
            {
                candidates.Add(watch.ProjectId);
            }
        }

        return candidates.Count switch
        {
            0 => new ProjectIdResolution.None(),
            1 => new ProjectIdResolution.Resolved(candidates.Single()),
            _ => new ProjectIdResolution.Ambiguous([.. candidates.Order(StringComparer.Ordinal)]),
        };
    }
}
