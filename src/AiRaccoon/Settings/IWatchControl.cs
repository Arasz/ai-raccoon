using AiRaccoon.Core.Watch;

namespace AiRaccoon.Settings;

/// <summary>
///     The CLI's way to register, remove and inspect watches through the server's watch service
///     (ADR-0075 amendment). A refusal throws <see cref="WatchRefusedException" />.
/// </summary>
internal interface IWatchControl
{
    Task<WatchAddResponse> AddAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task<WatchRemoveResponse> RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default);

    /// <summary>Live watch state for one project, or every project's watches when <paramref name="projectId" /> is null.</summary>
    Task<IReadOnlyList<WatchStatus>> StatusAsync(string? projectId, CancellationToken cancellationToken = default);
}

/// <summary>The server refused a watch call; <see cref="Kind" /> is the MCP refusal prefix and the message carries the CLI fix.</summary>
internal sealed class WatchRefusedException(string kind, string message, string? coveringPath) : Exception(message)
{
    public string Kind { get; } = kind;

    public string? CoveringPath { get; } = coveringPath;

    /// <summary>Builds the refusal with the fix for <paramref name="projectId" /> and <paramref name="path" /> appended.</summary>
    public static WatchRefusedException For(WatchRefusal refusal, string? projectId, string? path)
    {
        var project = projectId ?? "<project-id>";
        var fix = refusal.Kind switch
        {
            "watching-disabled" => $" Fix: ai-raccoon settings watch enable {project} true",
            "path-outside-scope" => $" Fix: ai-raccoon settings ingest scope add {project} {path ?? "<path>"}",
            "path-not-found" => " Fix: pass a path that exists on the server's machine.",
            "watch-overlap" => $" The watch '{refusal.CoveringPath}' already covers it; see 'ai-raccoon watch status {project}'.",
            "project-not-registered" =>
                $" Fix: look up this repository's id with 'ai-raccoon project id get --name <repo>', or register it with 'ai-raccoon project id register {project}'.",
            "project-retired" => " Fix: use the id it was folded into; 'ai-raccoon project id check <id>' tells which ids are known.",
            _ => string.Empty
        };
        return new WatchRefusedException(refusal.Kind, $"{refusal.Kind}: {refusal.Message}{fix}", refusal.CoveringPath);
    }
}

/// <summary>The graph that writes the bank directly (`encryption`) never reaches the server; watch verbs are not dispatched there.</summary>
internal sealed class DirectGraphWatchControl : IWatchControl
{
    public Task<WatchAddResponse> AddAsync(string projectId, string path, CancellationToken cancellationToken = default) => throw Refused();

    public Task<WatchRemoveResponse> RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default) => throw Refused();

    public Task<IReadOnlyList<WatchStatus>> StatusAsync(string? projectId, CancellationToken cancellationToken = default) => throw Refused();

    private static NotSupportedException Refused() =>
        new("ai-raccoon: watch registrations change only through the server; this command writes the bank directly");
}
