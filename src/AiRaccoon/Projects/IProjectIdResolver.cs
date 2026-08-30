namespace AiRaccoon.Projects;

/// <summary>
///     The outcome of default projectId resolution for a tool call that named no project:
///     exactly one candidate (<see cref="Resolved" />), several refused as ambiguous
///     (<see cref="Ambiguous" /> — ids sorted, never guessed between), or none (<see cref="None" />).
/// </summary>
public abstract record ProjectIdResolution
{
    private ProjectIdResolution()
    {
    }

    /// <summary>The single candidate; travels to the gate for its one canonicalization.</summary>
    public sealed record Resolved(string ProjectId) : ProjectIdResolution;

    /// <summary>Two or more candidates contain the cwd; ids sorted for a stable refusal message.</summary>
    public sealed record Ambiguous(IReadOnlyList<string> SortedIds) : ProjectIdResolution;

    /// <summary>No registered surface contains the cwd.</summary>
    public sealed record None : ProjectIdResolution;
}

/// <summary>
///     Resolves the default projectId for a tool call that named none, from the working directory.
/// </summary>
public interface IProjectIdResolver
{
    /// <summary>Probes the current working directory and classifies the candidates that contain it.</summary>
    Task<ProjectIdResolution> ResolveAsync(CancellationToken cancellationToken = default);
}
