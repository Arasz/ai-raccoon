using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     Serializes every test that reads or replaces the process-wide
///     <see cref="Core.Projects.ProjectIdAliasMap.Default" /> choke map (Package E): parallel
///     xUnit collections would otherwise let a loaded fixture map leak into a test asserting the
///     empty steady state, or vice versa. Membership rule: any test that touches Default directly
///     (replace/reset/fold/resolve) or transitively (ToolGate behind a migrated gate, the
///     IngestScopeKeys/AccessModePolicy/WatchConfigKeys helpers, SyncService merges) belongs here.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProjectIdAliasDefaultCollection
{
    public const string Name = "ProjectIdAliasMap.Default";
}
