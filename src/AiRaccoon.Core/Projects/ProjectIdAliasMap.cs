using System.Text.Json;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>A loser id and the canonical winner its rows fold into.</summary>
public sealed record ProjectIdAliasEntry(string Alias, string Canonical);

/// <summary>Durable loser-to-winner map for the single-project-id merge (air-merge plan P1): compiled into the binary and JSON-round-trippable, so pull-time fold and the ToolGate fold consume the same table with no bank FK.</summary>
public sealed class ProjectIdAliasMap
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>The plan's canonical-wins table: jsaa and ai-raccoon casing folds; single-fragment verbatims register as their own canonicals; qa-noise-project and manual-sweep delete (never fold).</summary>
    public static ProjectIdAliasMap Default { get; } = new(
        [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
        ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
        ["qa-noise-project", "manual-sweep"]);

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

    /// <summary>True and the winner when the id is a known loser or a canonical itself (self-mapped); false for dropped ids and true typos.</summary>
    public bool TryResolve(string projectId, out string? canonical)
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

    /// <summary>True when the id is deleted with a tombstone at merge time, never folded.</summary>
    public bool IsDropped(string projectId)
    {
        return _dropped.Contains(projectId);
    }

    /// <summary>Serializes the map for durable hand-off (plan artifact, settings snapshot); no bank schema involved.</summary>
    public string ToJson()
    {
        var payload = new AliasMapPayload([.. _aliases.Select(kv => new ProjectIdAliasEntry(kv.Key, kv.Value))], [.. _canonicals], [.. _dropped]);
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
