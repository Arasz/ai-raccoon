using System.Globalization;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Watch;

/// <summary>
///     Resolved watch config for one project: project entry wins over global; enable
///     defaults to false, concurrency to 4 (mirrors <c>AccessModePolicy.Resolve</c>).
/// </summary>
public sealed record WatchConfig(bool Enabled, IReadOnlyList<string> Scope, int Concurrency)
{
    public static WatchConfig Resolve(string projectId, Func<string, string?> settings)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        Guard.IsNotNull(settings);

        return new WatchConfig(
            Enabled: ParseBool(settings(WatchConfigKeys.EnabledProject(projectId)))
                     ?? ParseBool(settings(WatchConfigKeys.EnabledGlobal))
                     ?? false,
            Scope: WatchConfigKeys.ParseScope(settings(WatchConfigKeys.ScopeProject(projectId)))
                   ?? WatchConfigKeys.ParseScope(settings(WatchConfigKeys.ScopeGlobal))
                   ?? [],
            Concurrency: ParseInt(settings(WatchConfigKeys.ConcurrencyProject(projectId)))
                         ?? ParseInt(settings(WatchConfigKeys.ConcurrencyGlobal))
                         ?? 4);
    }

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
