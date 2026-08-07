using AiRaccoon.Access;
using AiRaccoon.Core;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using ModelContextProtocol;

namespace AiRaccoon.Tools;

/// <summary>
///     What every MCP tool does around its call: reject a blank project id, enforce the
///     project's access mode, and wrap the result in the envelope carrying the propose
///     tier's meta. One copy, so the seven tool classes cannot drift apart.
/// </summary>
public sealed class ToolGate(IMemoryAccessGuard access, IPromotionQueue queue)
{
    /// <summary>Rejects a blank project id, then throws access-denied when the mode is too low.</summary>
    public async Task RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new McpException("invalid-params: project_id is required");
        }

        await access.EnsureAsync(projectId, requirement, toolName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The envelope every tool returns: the payload plus what is waiting for review.</summary>
    public async Task<ApiEnvelope<T>> WrapAsync<T>(T data, CancellationToken cancellationToken) =>
        new(data, await queue.GetMetaAsync(cancellationToken).ConfigureAwait(false));
}
