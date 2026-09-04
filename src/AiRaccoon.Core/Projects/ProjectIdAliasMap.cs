using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>A loser id and the canonical winner its rows fold into.</summary>
public sealed record ProjectIdAliasEntry(string Alias, string Canonical);

/// <summary>Durable loser-to-winner map for the single-project-id merge (air-merge plan P1): compiled into the binary and JSON-round-trippable, so pull-time fold and the ToolGate fold consume the same table with no bank FK.</summary>
/// <remarks>
///     d-425 SHOULD-5: every lookup is <see cref="StringComparison.Ordinal" /> — case-SENSITIVE
///     by decision, recorded here as a non-goal rather than normalized. A mixed-case id that is
///     not an explicit entry (e.g. <c>JSAA</c>) is a distinct id and passes through untouched;
///     only an explicit alias entry folds (e.g. <c>AI-RACCOON</c> → <c>ai-raccoon</c>). The sync
///     CASE inherits the same semantics (built from <see cref="Aliases" /> verbatim).
/// </remarks>
public sealed class ProjectIdAliasMap
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    ///     The plan's canonical-wins table (2026-09-04 owner repair pass). Folds: the jsaa long name,
    ///     the ai-raccoon casing split, the pbi-badger-integration typo, the aib abbreviation, and
    ///     four minted guids — cfe47dab/024ef989/b0e32c16 read from their repo's
    ///     .ai-badger/project-id token file, 01a062f4 and 01a06ba4 attributed by entry content
    ///     (jsaa session notes and a JobSearchAiAssistant ingest entry; their tokens were never
    ///     written to any project dir). Canonicals: the single-fragment
    ///     verbatims plus ai-sheepdog, pi-badger-integration, and the server's own
    ///     __self_metrics__ instrumentation pseudo-project. Dropped: the QA/manual-sweep residue
    ///     plus the census residue the owner ruled noise — seven unattributed guids (early
    ///     implementation-test mints) and sixteen manual-probe ids — deleted with tombstones,
    ///     never folded.
    /// </summary>
    public static ProjectIdAliasMap Default { get; } = new(
        [
            new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"),
            new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon"),
            new ProjectIdAliasEntry("pbi-badger-integration", "pi-badger-integration"),
            new ProjectIdAliasEntry("aib", "ai-badger"),
            new ProjectIdAliasEntry("cfe47dab-5dfc-4749-9551-6a81f51c7beb", "ai-raccoon"),
            new ProjectIdAliasEntry("024ef989-26cc-4076-a8c2-e70712b0633d", "ai-badger"),
            new ProjectIdAliasEntry("b0e32c16-f502-4896-9b97-0bbee0fb321d", "jsaa"),
            new ProjectIdAliasEntry("01a062f4-fb77-767d-997d-924c90b68e32", "jsaa"),
            new ProjectIdAliasEntry("01a06ba4-7120-7a79-b581-ebf48cbb88f9", "jsaa"),
        ],
        [
            "jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness",
            "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks",
            "ai-sheepdog", "pi-badger-integration", "__self_metrics__",
        ],
        [
            "qa-noise-project", "manual-sweep",
            // 2026-09-04 owner repair pass: test guids and manual-probe residue, purged on apply.
            "00000000-0000-7000-8000-000000000000",
            "0197f3e0-7c8e-7f57-a1f5-3a6b9c2d4e01",
            "01a03024-f800-71a1-be87-92dd7cfee216",
            "01a0302f-b316-71ff-8e16-57b0c33c7907",
            "01a030af-6444-775d-9495-35908180320c",
            "01a04d9d-9417-75f2-a2ba-730fcfba8411", // 'memory-roundtrip-test' — PI-MEMORY-ROUNDTRIP-MARKER
            "01a04d9e-f272-74c1-8a8d-f2eaff21e6f4", // early mint named 'ai-badger' during ADR-0089 testing; not the repo's token
            "acme", "installed-140-verify", "manual-13x-probe", "manual-150-check",
            "manual-160-check", "manual-d1d2d3-verify", "manualtest-tar2", "memtest-x",
            "multi", "none", "pi-post-smoke", "refused",
            "release-check-120", "release-check-130", "release-check-131",
            "verify-fixes-probe", "wsprobe-2",
        ]);

    private readonly Dictionary<string, string> _aliases;
    private readonly HashSet<string> _canonicals;
    private readonly HashSet<string> _dropped;

    public ProjectIdAliasMap(IEnumerable<ProjectIdAliasEntry> aliases, IEnumerable<string> canonicals, IEnumerable<string> dropped)
    {
        Guard.IsNotNull(aliases);
        Guard.IsNotNull(canonicals);
        Guard.IsNotNull(dropped);
        _aliases = aliases.ToDictionary(e => e.Alias, e => e.Canonical, StringComparer.Ordinal);
        _canonicals = new HashSet<string>(canonicals, StringComparer.Ordinal);
        _dropped = new HashSet<string>(dropped, StringComparer.Ordinal);
    }

    public IReadOnlyList<ProjectIdAliasEntry> Aliases =>
        _aliases.Select(kv => new ProjectIdAliasEntry(kv.Key, kv.Value)).ToList();

    public IReadOnlyList<string> Canonicals => [.. _canonicals];

    public IReadOnlyList<string> Dropped => [.. _dropped];

    /// <summary>True and the winner when the id is a known loser or a canonical itself (self-mapped); false for dropped ids and true typos.</summary>
    public bool TryResolve(string projectId, [NotNullWhen(true)] out string? canonical)
    {
        if (_aliases.TryGetValue(projectId, out canonical))
        {
            return true;
        }

        if (_canonicals.Contains(projectId))
        {
            canonical = projectId;
            return true;
        }

        canonical = null;
        return false;
    }

    /// <summary>
    ///     The choke's single fold (air-merge P3/P4, review M4c): guid D-form first, then the alias
    ///     winner. Canonicals, true typos and drop-candidates come back untouched — refusing a typo
    ///     is the registration guard's job, and a dropped id is deleted, never folded. Blank input
    ///     passes through: the key factories derive their prefixes from an empty id.
    /// </summary>
    public string Fold(string projectId)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return projectId;
        }

        var canonical = ProjectId.Canonicalize(projectId);
        return TryResolve(canonical, out var winner) ? winner : canonical;
    }

    /// <summary>True when the id is deleted with a tombstone at merge time, never folded.</summary>
    public bool IsDropped(string projectId)
    {
        return _dropped.Contains(projectId);
    }

    /// <summary>Serializes the map for durable hand-off (plan artifact, settings snapshot); no bank schema involved.</summary>
    public string ToJson()
    {
        var payload = new AliasMapPayload([.. Aliases], [.. Canonicals], [.. Dropped]);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Deserializes a map written by <see cref="ToJson" />.</summary>
    public static ProjectIdAliasMap FromJson(string json)
    {
        Guard.IsNotNullOrWhiteSpace(json);
        var payload = JsonSerializer.Deserialize<AliasMapPayload>(json, JsonOptions);
        Guard.IsNotNull(payload);
        return new ProjectIdAliasMap(payload.Aliases, payload.Canonicals, payload.Dropped);
    }

    private sealed record AliasMapPayload(ProjectIdAliasEntry[] Aliases, string[] Canonicals, string[] Dropped);
}
