using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     E2E tests mutate the process environment (MCP_TRANSPORT, AIRACCOON_DATA_ROOT) to boot the
///     real server, so they must run serially against one another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class E2ETestCollection
{
    public const string Name = "E2E";
}
