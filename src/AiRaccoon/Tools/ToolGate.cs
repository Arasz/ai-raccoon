using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Projects;
using ModelContextProtocol;

namespace AiRaccoon.Tools;

/// <summary>
///     What every MCP tool does around its call: refuse while a model migration is open
///     (ADR-0076 — "lock all DB operations for the duration"), reject a blank project id —
///     resolving it from the working directory when a resolver is wired and the call named none —
///     enforce the project's access mode, and wrap the result in the envelope carrying the
///     propose tier's meta. One copy, so the tool classes cannot drift apart.
/// </summary>
/// <remarks>
///     d-425 SHOULD-1 / d-426 SHOULD-5: <paramref name="migrationGate" /> is REQUIRED (no
///     nullable default) — a gate-less construction used to default every test-harness build to
///     pass-through, silently skipping the P3 fold. Fail-closed by construction: forgetting the
///     gate is a compile error, and an explicitly unmigrated gate still folds nothing.
/// </remarks>
public sealed class ToolGate(
    IMemoryAccessGuard access,
    IPromotionQueue queue,
    IModelMigrationStore migrations,
    IProjectRegistrationGuard registration,
    IProjectIdsMigrationGate migrationGate,
    IProjectIdResolver? resolver = null) : IToolGate
{
    /// <summary>Refuses while a migration is open. Nothing else — the check a tool with no project yet can still make.</summary>
    public async Task RequireBankAvailableAsync(string toolName, CancellationToken cancellationToken)
    {
        if (await migrations.HasOpenModelMigrationAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ModelMigrationInProgressException(
                $"ai-raccoon: a model migration is in progress; try again once it finishes ({toolName})");
        }
    }

    /// <summary>
    ///     Refuses while a migration is open, then rejects a blank project id — a blank id is
    ///     resolved from the working directory when a resolver is wired (Resolved flows through the
    ///     single canonicalization; Ambiguous/None refuse with the probed cwd in the message) —
    ///     canonicalizes it (ADR-0089 decision 2), folds a known loser to its winner once the P2
    ///     finished marker exists (air-merge P3, review M1 — no fold until migrated, so an
    ///     unmigrated bank behaves exactly as before), refuses a write under a retired (dropped)
    ///     id with the repair attribution (Package E — dropped ids are deleted, never folded, so
    ///     resurrecting them by write is pure harm; reads still pass through), throws access-denied
    ///     when the mode is too low, and only then refuses an unregistered id on a write
    ///     (decision 3 — reads pass through untouched). Registration is checked last so an
    ///     unauthorized caller cannot learn whether an id is registered from the refusal shape.
    ///     Returns the canonical id for the caller to carry to storage.
    /// </summary>
    public async Task<string> RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken)
    {
        await RequireBankAvailableAsync(toolName, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = await ResolveFromCwdAsync(cancellationToken).ConfigureAwait(false);
        }

        var canonical = ProjectId.Canonicalize(projectId);
        if (await migrationGate.IsMigratedAsync(cancellationToken).ConfigureAwait(false))
        {
            canonical = ProjectIdAliasMap.Default.Fold(canonical);
            if (requirement is not AccessRequirement.Read && ProjectIdAliasMap.Default.IsDropped(canonical))
            {
                throw new RetiredProjectException(canonical);
            }
        }

        await access.EnsureAsync(canonical, requirement, toolName, cancellationToken).ConfigureAwait(false);
        await registration.EnsureAsync(canonical, requirement, cancellationToken).ConfigureAwait(false);
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
            switch (await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false))
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
        new(data, await queue.GetMetaAsync(projectId, cancellationToken).ConfigureAwait(false));
}
