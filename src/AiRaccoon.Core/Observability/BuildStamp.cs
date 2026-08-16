using System.Globalization;

namespace AiRaccoon.Core.Observability;

/// <summary>
///     Parses <see cref="IBuildStamp" /> fields from the SDK's own attribute text: version and
///     commit sha split off "version+sha" (<see cref="System.Reflection.AssemblyInformationalVersionAttribute" />,
///     the SDK's own SourceRevisionId append), commit timestamp from a "CommitTimestamp"
///     <see cref="System.Reflection.AssemblyMetadataAttribute" /> value (GitStamp.targets). Pure and
///     reflection-free so the unavailable-stamp contract is testable without a real assembly or git.
/// </summary>
public sealed record BuildStamp(string Version, string Commit, DateTimeOffset? CommitTimestamp) : IBuildStamp
{
    public const string UnknownCommit = "unknown";

    public static BuildStamp Parse(string? informationalVersion, string? commitTimestampText)
    {
        var (version, commit) = SplitInformationalVersion(informationalVersion);
        return new BuildStamp(version, commit, ParseCommitTimestamp(commitTimestampText));
    }

    private static (string Version, string Commit) SplitInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
        {
            return (string.Empty, UnknownCommit);
        }

        var parts = informationalVersion.Split('+', 2);
        return parts.Length == 2 && parts[1].Length > 0 ? (parts[0], parts[1]) : (parts[0], UnknownCommit);
    }

    private static DateTimeOffset? ParseCommitTimestamp(string? text) =>
        !string.IsNullOrEmpty(text) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
