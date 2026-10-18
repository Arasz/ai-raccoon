using System.Globalization;
using AiRaccoon.Core.Ingestion;
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

        if (status.SkippedCount == 0)
        {
            yield break;
        }

        yield return $"skipped: {status.SkippedCount}";
        foreach (var file in status.Skipped)
        {
            yield return $"  {file.Path} ({file.Reason.ToWireName()})";
        }

        if (status.SkippedCount > status.Skipped.Count)
        {
            yield return $"  … {status.SkippedCount - status.Skipped.Count} more";
        }
    }

    private static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
