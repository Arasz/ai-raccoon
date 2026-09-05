using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>A loser id and the canonical winner its rows fold into.</summary>
public sealed record ProjectIdAliasEntry(string Alias, string Canonical);

/// <summary>One-shot loser-to-winner map for the project-ids repair: file-loaded per invocation and JSON-round-trippable, so the CLI dry-run planner and the server apply job consume the same table with no bank FK and no compiled-in ids.</summary>
/// <remarks>
///     d-425 SHOULD-5: every lookup is <see cref="StringComparison.Ordinal" /> — case-SENSITIVE
///     by decision, recorded here as a non-goal rather than normalized. A mixed-case id that is
///     not an explicit entry (e.g. <c>OLD-ID</c>) is a distinct id and passes through untouched;
///     only an explicit alias entry folds (e.g. <c>Old-Id</c> → <c>old-id</c>). The sync
///     CASE inherits the same semantics (built from <see cref="Aliases" /> verbatim).
///     <para>
///         ADR-0099: the public binary ships no machine-local ids — <see cref="Default" /> is
///         empty by design (steady-state folds are pass-through). Operators with fragment ids
///         supply their own map per repair via <c>repair project-ids --map &lt;path&gt;</c>;
///         the dry run without <c>--map</c> writes an editable template beside the bank.
///     </para>
/// </remarks>
public sealed class ProjectIdAliasMap
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <summary>The empty map: no aliases, no canonicals, no drops. <see cref="Fold" /> degrades to guid D-form normalization only.</summary>
    public static ProjectIdAliasMap Empty { get; } = new([], [], []);

    private static readonly Lock DefaultGate = new();

    private static ProjectIdAliasMap _default = Empty;

    /// <summary>
    ///     The steady-state map consumed by every choke point (ToolGate, watch boundaries, key
    ///     helpers, sync). Empty by design (ADR-0099) until a map change reloads it via
    ///     <see cref="ReplaceDefault" /> — the sync pull arm reloads after merging a replica's rows;
    ///     a repair apply persists the one-shot map to the durable table for hosts to pick up.
    ///     An empty map is pass-through by definition — every choke behaves exactly as before the
    ///     durable map existed.
    /// </summary>
    public static ProjectIdAliasMap Default
    {
        get
        {
            lock (DefaultGate)
            {
                return _default;
            }
        }
    }

    /// <summary>Reloads the choke-point cache after the durable map changed (repair apply, sync pull).</summary>
    public static void ReplaceDefault(ProjectIdAliasMap map)
    {
        Guard.IsNotNull(map);
        lock (DefaultGate)
        {
            _default = map;
        }
    }

    /// <summary>Restores the empty steady state. Tests own this: every test that replaces the cache resets it.</summary>
    public static void ResetDefault()
    {
        lock (DefaultGate)
        {
            _default = Empty;
        }
    }

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

    /// <summary>True when the map carries no aliases, canonicals, or drops — steady-state pass-through.</summary>
    public bool IsEmpty => _aliases.Count == 0 && _canonicals.Count == 0 && _dropped.Count == 0;

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
        return ToJson(indented: false);
    }

    /// <summary>Serializes the map; <paramref name="indented" /> selects the human-editable template shape.</summary>
    public string ToJson(bool indented)
    {
        var payload = new AliasMapPayload([.. Aliases], [.. Canonicals], [.. Dropped]);
        return JsonSerializer.Serialize(payload, indented ? IndentedJsonOptions : JsonOptions);
    }

    /// <summary>Deserializes a map written by <see cref="ToJson" />. Null alias entries and null alias/canonical spellings are refused — a null winner would otherwise fold an id to null downstream.</summary>
    public static ProjectIdAliasMap FromJson(string json)
    {
        Guard.IsNotNullOrWhiteSpace(json);
        var payload = JsonSerializer.Deserialize<AliasMapPayload>(json, JsonOptions);
        Guard.IsNotNull(payload);
        if (payload.Aliases is null || payload.Canonicals is null || payload.Dropped is null)
        {
            throw new ArgumentException("ai-raccoon: project-ids alias map holds a null aliases, canonicals, or dropped section.");
        }

        foreach (var entry in payload.Aliases)
        {
            if (entry is null || entry.Alias is null || entry.Canonical is null)
            {
                throw new ArgumentException("ai-raccoon: project-ids alias map holds a null alias entry or null alias/canonical spelling.");
            }
        }

        return new ProjectIdAliasMap(payload.Aliases!, payload.Canonicals, payload.Dropped);
    }

    /// <summary>Loads a map written by <see cref="ToJson" /> (or the dry-run template) from disk. Missing files throw <see cref="FileNotFoundException" /> naming the path; malformed JSON throws <see cref="JsonException" /> naming the path.</summary>
    public static ProjectIdAliasMap LoadFromFile(string path)
    {
        Guard.IsNotNullOrWhiteSpace(path);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new FileNotFoundException($"project-ids alias map not found: '{path}'", path, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FileNotFoundException($"project-ids alias map not found: '{path}'", path, ex);
        }

        try
        {
            return FromJson(json);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"project-ids alias map at '{path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    private sealed record AliasMapPayload(ProjectIdAliasEntry[] Aliases, string[] Canonicals, string[] Dropped);
}
