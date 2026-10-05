using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>One per-project settings prefix; <see cref="ExcludesGlobal" /> marks a prefix whose <c>global</c> owner is the global key, not a project.</summary>
public sealed record ProjectSettingsPrefix(string Prefix, bool ExcludesGlobal);

/// <summary>The settings keys owned by one project, defined once for the settings endpoint, the census and the repair.</summary>
public static class ProjectSettingsKeys
{
    private const string GlobalOwner = "global";

    /// <summary>Every per-project prefix, in the order the repair pairs a loser's keys with its winner's.</summary>
    public static IReadOnlyList<ProjectSettingsPrefix> Prefixes { get; } =
    [
        new("ingest.scope.", ExcludesGlobal: true),
        new("watch.scope.", ExcludesGlobal: false),
        new("watch.enabled.", ExcludesGlobal: true),
        new("watch.concurrency.", ExcludesGlobal: true),
        new("access.mode.project:", ExcludesGlobal: false)
    ];

    /// <summary>True and the owner when <paramref name="key" /> is a per-project key; the owner may be blank.</summary>
    public static bool TryGetProjectId(string key, [NotNullWhen(true)] out string? projectId)
    {
        Guard.IsNotNull(key);
        foreach (var prefix in Prefixes)
        {
            if (!key.StartsWith(prefix.Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var owner = key[prefix.Prefix.Length..];
            if (prefix.ExcludesGlobal && owner == GlobalOwner)
            {
                break;
            }

            projectId = owner;
            return true;
        }

        projectId = null;
        return false;
    }

    /// <summary>The per-project <paramref name="key" /> with its owner replaced by <paramref name="projectId" />.</summary>
    public static string WithProjectId(string key, string projectId)
    {
        Guard.IsNotNull(key);
        Guard.IsNotNullOrWhiteSpace(projectId);
        var prefix = Prefixes.FirstOrDefault(p => key.StartsWith(p.Prefix, StringComparison.Ordinal));
        if (prefix is null || !TryGetProjectId(key, out _))
        {
            ThrowHelper.ThrowArgumentException(nameof(key), $"'{key}' is not a per-project settings key");
        }

        var replacement = prefix.Prefix + projectId;
        if (!TryGetProjectId(replacement, out _))
        {
            ThrowHelper.ThrowArgumentException(nameof(projectId), $"'{replacement}' is a machine-global settings key");
        }

        return replacement;
    }

    /// <summary>Every per-project key of <paramref name="projectId" />, in <see cref="Prefixes" /> order.</summary>
    public static IReadOnlyList<string> KeysFor(string projectId) =>
        [.. Prefixes.Where(p => !p.ExcludesGlobal || projectId != GlobalOwner).Select(p => p.Prefix + projectId)];
}
