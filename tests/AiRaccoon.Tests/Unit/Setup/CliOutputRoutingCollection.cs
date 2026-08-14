using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Serializes the CLI output-routing tests, which redirect the process-global Console.Out to
///     prove rendered CLI text never reaches real stdout.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliOutputRoutingCollection
{
    public const string Name = "CliOutputRouting-Serial";
}
