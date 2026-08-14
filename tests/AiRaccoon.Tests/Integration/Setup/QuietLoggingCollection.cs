using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     Serializes tests that redirect the process-global Console.Out/Console.Error to prove
///     quiet mode never leaks to stdout/stderr.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class QuietLoggingCollection
{
    public const string Name = "QuietLogging";
}
