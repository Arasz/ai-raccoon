using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Projects;

/// <summary>Enforces ADR-0089 decision 3: a write to an unregistered id is refused, guid or not.</summary>
public interface IProjectRegistrationGuard
{
    /// <summary>
    ///     Reads are allowed unconditionally — same shape as <see cref="IMemoryAccessGuard.EnsureAsync" />.
    ///     For a write/destructive <paramref name="requirement" />, throws
    ///     <see cref="UnregisteredProjectException" /> when the canonical
    ///     <paramref name="projectId" /> has no registry row and the bank holds no rows for it
    ///     either. A registered-but-empty project passes silently; a legacy id with rows but no
    ///     registration passes with a one-time warning.
    /// </summary>
    Task EnsureAsync(string projectId, AccessRequirement requirement, CancellationToken cancellationToken = default);
}
