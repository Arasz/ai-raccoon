using AiRaccoon.Core.Access;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Projects;

/// <summary>
///     Enforces ADR-0089 decision 3: a project exists when it is registered, not when it is first
///     written to. The single compatibility exception is a raw-text id the bank already holds rows
///     for — it keeps working, once warned.
/// </summary>
public sealed partial class ProjectRegistrationGuard(IProjectRegistry registry, ILogger<ProjectRegistrationGuard> logger)
    : IProjectRegistrationGuard
{
    // The docs promise a ONE-time warning per legacy id; the guard is a singleton, so this set
    // makes that true per process rather than per call.
    private readonly HashSet<string> warnedLegacyIds = [];

    /// <inheritdoc />
    public async Task EnsureAsync(string projectId, AccessRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        // Reads are allowed unconditionally; skip the registry/rows lookups entirely — the same
        // shape as MemoryAccessGuard.EnsureAsync's early return for AccessRequirement.Read.
        if (requirement == AccessRequirement.Read)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (await registry.IsRegisteredAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await registry.HasRowsAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            throw new UnregisteredProjectException(projectId);
        }

        if (warnedLegacyIds.Add(projectId))
        {
            Log.LegacyProjectIdAccepted(logger, projectId);
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 433, Level = LogLevel.Warning,
            Message = "project id {ProjectId} is not registered; it works because the bank already holds rows for it. Convert it with `project id convert`.")]
        public static partial void LegacyProjectIdAccepted(ILogger logger, string projectId);
    }
}
