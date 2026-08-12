using AiRaccoon.Core.Access;

namespace AiRaccoon.Tools;

public interface IToolGate
{
    /// <summary>Rejects a blank project id, then throws access-denied when the mode is too low.</summary>
    Task RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken);

    /// <summary>
    ///     The envelope every tool returns: the payload plus what is waiting for the calling
    ///     project — the whole bank only when the call itself named no project.
    /// </summary>
    Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken);
}
