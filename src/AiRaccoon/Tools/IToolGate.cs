using AiRaccoon.Core.Access;

namespace AiRaccoon.Tools;

public interface IToolGate
{
    /// <summary>Refuses while a model migration is open — the one check a tool with no project yet can still make.</summary>
    Task RequireBankAvailableAsync(string toolName, CancellationToken cancellationToken);

    /// <summary>
    ///     Rejects a blank project id, canonicalizes it (ADR-0089 decision 2), throws access-denied
    ///     when the mode is too low, and returns the canonical id for the caller to carry downstream.
    /// </summary>
    Task<string> RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
        CancellationToken cancellationToken);

    /// <summary>
    ///     The envelope every tool returns: the payload plus what is waiting for the calling
    ///     project — the whole bank only when the call itself named no project.
    /// </summary>
    Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken);
}
