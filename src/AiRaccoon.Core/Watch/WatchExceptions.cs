namespace AiRaccoon.Core.Watch;

/// <summary>Watching is disabled for the project — the MCP tool maps this to `watching-disabled`.</summary>
public sealed class WatchDisabledException(string projectId)
    : InvalidOperationException($"Watching is disabled for project '{projectId}'.");

/// <summary>
///     The candidate path is contained by (or loses the tie-break against) an already-registered
///     watch — the MCP tool maps this to `watch-overlap`. Nothing is written.
/// </summary>
public sealed class WatchOverlapException(string path, string coveringPath)
    : InvalidOperationException($"Path '{path}' is already covered by watch '{coveringPath}'.")
{
    public string Path { get; } = path;

    public string CoveringPath { get; } = coveringPath;
}
