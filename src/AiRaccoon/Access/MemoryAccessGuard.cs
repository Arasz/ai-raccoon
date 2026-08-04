using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using ModelContextProtocol;

namespace AiRaccoon.Access;

/// <summary>Reads the mode settings from the bank and enforces requirements at the tool boundary.</summary>
public sealed class MemoryAccessGuard(IMemoryStore store) : IMemoryAccessGuard
{
    public async Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var perProjectRaw = await store.GetSettingAsync(
                AccessModePolicy.ProjectSettingKey(projectId), cancellationToken)
            .ConfigureAwait(false);
        if (AccessModePolicy.Parse(perProjectRaw) is { } perProject)
        {
            return perProject;
        }

        var globalRaw = await store.GetSettingAsync(AccessModePolicy.GlobalSettingKey, cancellationToken)
            .ConfigureAwait(false);
        return AccessModePolicy.Resolve(AccessModePolicy.Parse(globalRaw), null);
    }

    public async Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken = default)
    {
        // Reads are allowed in every mode; skip the settings lookup entirely.
        if (requirement == AccessRequirement.Read)
        {
            return;
        }

        var mode = await ResolveAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (AccessModePolicy.Allows(mode, requirement))
        {
            return;
        }

        var required = AccessModePolicy.RequiredFor(requirement);
        throw new McpException(
            $"access-denied: {toolName} requires mode {AccessModePolicy.Serialize(required)} (current {AccessModePolicy.Serialize(mode)})");
    }
}
