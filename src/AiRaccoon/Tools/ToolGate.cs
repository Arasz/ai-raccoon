using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using ModelContextProtocol;

namespace AiRaccoon.Tools;

/// <summary>
///     What every MCP tool does around its call: reject a blank project id, enforce the
///     project's access mode, and wrap the result in the envelope carrying the propose
///     tier's meta. One copy, so the seven tool classes cannot drift apart.
/// </summary>
public sealed class ToolGate(IMemoryAccessGuard access, IPromotionQueue queue) : IToolGate
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

    /// <summary>
    ///     The envelope every tool returns: the payload plus what is waiting for the calling
    ///     project — the whole bank only when the call itself named no project.
    /// </summary>
    public async Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken) =>
        new(data, await queue.GetMetaAsync(projectId, cancellationToken).ConfigureAwait(false));
}
