using AiRaccoon.Core.Access;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Projects;

/// <summary>Enforces ADR-0089 decision 3: a call under an unregistered id is refused, read or write, guid or not.</summary>
public interface IProjectRegistrationGuard
{
    /// <summary>
    ///     Throws <see cref="UnregisteredProjectException" /> when the canonical
    ///     <paramref name="projectId" /> has no registry row and the bank holds no rows for it, for
    ///     every <paramref name="requirement" />. A legacy id with rows passes with a one-time
    ///     warning; reads never register an id.
    /// </summary>
    Task EnsureAsync(string projectId, AccessRequirement requirement, CancellationToken cancellationToken = default);
}
