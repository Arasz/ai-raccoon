using AiRaccoon.Core.Access;

namespace AiRaccoon.Access;

/// <summary>Resolves the effective access mode for a project and enforces tool requirements (FR-NM-2; see docs/work/features-native-memory/native-memory.feature).</summary>
public interface IMemoryAccessGuard
{
    /// <summary>Resolves the effective mode: per-project setting, else global default, else rw.</summary>
    Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Throws an access-denied MCP error when the project's mode does not allow the requirement.</summary>
    Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken = default);
}
