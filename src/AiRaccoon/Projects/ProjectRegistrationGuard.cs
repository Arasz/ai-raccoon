using AiRaccoon.Core.Access;
using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Projects;

/// <summary>
///     Enforces ADR-0089 decision 3 for every call, reads included: a project exists when it is
///     registered. An unregistered id the bank already holds rows for keeps working, warned once.
///     Until a project-ids repair finishes, an unregistered raw-text id auto-registers on a write;
///     reads never register.
/// </summary>
public sealed partial class ProjectRegistrationGuard(
    IProjectRegistry registry,
    ILogger<ProjectRegistrationGuard> logger,
    IProjectIdsMigrationGate migrationGate)
    : IProjectRegistrationGuard
{
    // One warning per legacy id per process; the guard is a singleton and tool calls run concurrently.
    private readonly Lock warnedGate = new();
    private readonly HashSet<string> warnedLegacyIds = [];

    /// <inheritdoc />
    public async Task EnsureAsync(string projectId, AccessRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);

        if (await registry.IsRegisteredAsync(projectId, cancellationToken))
        {
            return;
        }

        if (await registry.HasRowsAsync(projectId, cancellationToken))
        {
            WarnOnce(projectId);
            return;
        }

        // Pre-migration compatibility for writes only: a raw-text id auto-registers. A guid can
        // never be a legacy row owner, and once the repair finished every winner is registered.
        if (requirement is not AccessRequirement.Read
            && !Guid.TryParse(projectId, out _)
            && !await migrationGate.IsMigratedAsync(cancellationToken))
        {
            await registry.RegisterAsync(projectId, projectId, cancellationToken);
            return;
        }

        throw new UnregisteredProjectException(projectId);
    }

    private void WarnOnce(string projectId)
    {
        lock (warnedGate)
        {
            if (!warnedLegacyIds.Add(projectId))
            {
                return;
            }
        }

        Log.LegacyProjectIdAccepted(logger, projectId, Remedy(projectId));
    }

    private static string Remedy(string projectId) =>
        Guid.TryParse(projectId, out _)
            ? $"register it with 'ai-raccoon project id register {projectId}'"
            : "fold it into a registered project with 'ai-raccoon repair project-ids --map <file> --apply', " +
              $"or keep it with 'ai-raccoon project id register {projectId}'";

    internal static partial class Log
    {
        [LoggerMessage(EventId = 433, Level = LogLevel.Warning,
            Message = "project id {ProjectId} is not registered; it works because the bank already holds rows for it. To stop this warning, {Remedy}.")]
        public static partial void LegacyProjectIdAccepted(ILogger logger, string projectId, string remedy);
    }
}
