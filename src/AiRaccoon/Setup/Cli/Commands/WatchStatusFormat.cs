using System.Globalization;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One `watch status` block: a `name: value` line per fact, optional facts only when present.</summary>
internal static class WatchStatusFormat
{
    internal static IEnumerable<string> Render(WatchStatus status)
    {
        yield return $"project: {status.ProjectId}";
        yield return $"path: {status.Path}";
        yield return $"state: {status.State.ToString().ToLowerInvariant()}";
        yield return $"lastSync: {(status.LastSync is { } lastSync ? FormatInstant(lastSync) : "never")}";
        if (!string.IsNullOrEmpty(status.LastError))
        {
            yield return $"lastError: {status.LastError}";
        }
    }

    private static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
