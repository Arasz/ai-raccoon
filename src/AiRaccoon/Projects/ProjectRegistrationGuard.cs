using AiRaccoon.Core.Access;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Projects;

/// <summary>
///     Enforces ADR-0089 decision 3: a project exists when it is registered, not when it is first
///     written to. The single compatibility exception is a raw-text id the bank already holds rows
///     for — it keeps working, once warned. Refusal itself is mechanically gated on the P2
///     finished marker (air-merge P3, review M1): until a requested project-ids repair completes,
///     an unregistered verbatim id auto-registers exactly as before, so the winners' own writes
///     can never refuse on an unmigrated bank.
/// </summary>
public sealed partial class ProjectRegistrationGuard(
    IProjectRegistry registry,
    ILogger<ProjectRegistrationGuard> logger,
    IProjectIdsMigrationGate migrationGate)
    : IProjectRegistrationGuard
{
    // The docs promise a ONE-time warning per legacy id; the guard is a singleton, so this set
    // makes that true per process rather than per call. Locked: tool calls run concurrently.
    private readonly object warnedGate = new();
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

        var hasRows = await registry.HasRowsAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (!hasRows)
        {
            // Pre-migration compatibility: first-write auto-registers a verbatim id (a guid spelling
            // outside the registry is still refused — it can never be a legacy row owner). Once the
            // P2 marker exists the repair has registered every winner, so an unregistered id with
            // no rows is a true typo (or a deleted drop-candidate): refuse it, guid or not, and
            // register nothing as a side effect.
            if (!await migrationGate.IsMigratedAsync(cancellationToken).ConfigureAwait(false)
                && !Guid.TryParse(projectId, out _))
            {
                await registry.RegisterAsync(projectId, projectId, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new UnregisteredProjectException(projectId);
        }

        lock (warnedGate)
        {
            if (warnedLegacyIds.Add(projectId))
            {
                Log.LegacyProjectIdAccepted(logger, projectId);
            }
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 433, Level = LogLevel.Warning,
            Message = "project id {ProjectId} is not registered; it works because the bank already holds rows for it. Convert it with `project id convert`.")]
        public static partial void LegacyProjectIdAccepted(ILogger logger, string projectId);
    }
}
