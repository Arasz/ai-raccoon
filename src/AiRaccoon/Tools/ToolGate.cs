using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Projects;
using ModelContextProtocol;

namespace AiRaccoon.Tools;

/// <summary>
///     What every MCP tool does around its call: refuse while a model migration is open
///     (ADR-0076), resolve a blank project id from the working directory, fold and refuse retired
///     ids, enforce the project's access mode and registration, and wrap the result in the
///     envelope carrying the propose tier's meta. One copy, so the tool classes cannot drift apart.
/// </summary>
public sealed class ToolGate(
    IMemoryAccessGuard access,
    IPromotionQueue queue,
    IModelMigrationStore migrations,
    IProjectRegistrationGuard registration,
    IProjectIdResolver? resolver = null) : IToolGate
{
    /// <summary>Refuses while a migration is open. Nothing else — the check a tool with no project yet can still make.</summary>
    public async Task RequireBankAvailableAsync(string toolName, CancellationToken cancellationToken)
    {
        if (await migrations.HasOpenModelMigrationAsync(cancellationToken))
        {
            throw new ModelMigrationInProgressException(
                $"ai-raccoon: a model migration is in progress; try again once it finishes ({toolName})");
        }
    }

    /// <summary>
    ///     Refuses while a migration is open, resolves a blank id from the working directory, then
    ///     runs the id through <see cref="ProjectIdAliasMap.Apply" /> on the default map: an alias
    ///     folds to its winner, and a retired (dropped) id is refused on every requirement. Then
    ///     the access mode is enforced, and only then registration, so an unauthorized caller
    ///     cannot learn whether an id is registered. Returns the id to carry to storage.
    /// </summary>
    public async Task<string> RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken)
    {
        await RequireBankAvailableAsync(toolName, cancellationToken);

        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = await ResolveFromCwdAsync(cancellationToken);
        }

        var folded = ProjectIdAliasMap.Default.Apply(projectId);
        if (folded.Dropped)
        {
            throw new RetiredProjectException(folded.ProjectId);
        }

        var canonical = folded.ProjectId;
        await access.EnsureAsync(canonical, requirement, toolName, cancellationToken);
        await registration.EnsureAsync(canonical, requirement, cancellationToken);
        return canonical;
    }

    /// <summary>
    ///     The blank-id branch: consult the resolver when one is wired — its Resolved id re-enters
    ///     the normal canonicalize/access/registration chain unchanged — and refuse Ambiguous or
    ///     None with the probed working directory in the message. With no resolver wired, the same
    ///     enriched None refusal fires; the cwd is still probed so the message tells the caller
    ///     what was searched for.
    /// </summary>
    private async Task<string> ResolveFromCwdAsync(CancellationToken cancellationToken)
    {
        if (resolver is not null)
        {
            switch (await resolver.ResolveAsync(cancellationToken))
            {
                case ProjectIdResolution.Resolved resolved when !string.IsNullOrWhiteSpace(resolved.ProjectId):
                    return resolved.ProjectId;
                case ProjectIdResolution.Ambiguous ambiguous:
                    throw new McpException(
                        $"invalid-params: projectId is ambiguous from cwd {Environment.CurrentDirectory}: " +
                        $"candidates {string.Join(", ", ambiguous.SortedIds)}");
            }
        }

        throw new McpException(
            $"invalid-params: projectId is required (no registered project's scope contains cwd " +
            $"{Environment.CurrentDirectory}; pass projectId explicitly, or register this directory with " +
            "memory_watch_add / settings ingest scope add)");
    }

    /// <summary>
    ///     The envelope every tool returns: the payload plus what is waiting for the calling
    ///     project — the whole bank only when the call itself named no project.
    /// </summary>
    public async Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken) =>
        new(data, await queue.GetMetaAsync(projectId, cancellationToken));
}
