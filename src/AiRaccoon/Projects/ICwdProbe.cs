namespace AiRaccoon.Projects;

/// <summary>
///     Where the server process was started — the seam that lets tests pin a working directory
///     without mutating the process-wide one. For a stdio MCP server the meaningful value is the
///     directory the host spawned it from (pi, claude, and hermes alike launch per project),
///     which is exactly what the default implementation reads.
/// </summary>
public interface ICwdProbe
{
    /// <summary>The directory the default-resolution containment check runs against.</summary>
    string CurrentDirectory { get; }
}
