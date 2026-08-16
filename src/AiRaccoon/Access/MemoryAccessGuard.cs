using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Access;

/// <summary>Reads the mode settings from the bank and enforces requirements at the tool boundary.</summary>
public sealed class MemoryAccessGuard(IMemoryStore store) : IMemoryAccessGuard
{
    public async Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var settings = await store.GetSettingsByPrefixAsync(AccessModePolicy.SettingKeyPrefix, cancellationToken)
            .ConfigureAwait(false);

        settings.TryGetValue(AccessModePolicy.GlobalSettingKey, out var globalRaw);
        settings.TryGetValue(AccessModePolicy.ProjectSettingKey(projectId), out var perProjectRaw);
        return AccessModePolicy.Resolve(AccessModePolicy.Parse(globalRaw), AccessModePolicy.Parse(perProjectRaw));
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
        throw new AccessDeniedException(
            $"{toolName} requires mode {AccessModePolicy.Serialize(required)} (current {AccessModePolicy.Serialize(mode)})");
    }
}
