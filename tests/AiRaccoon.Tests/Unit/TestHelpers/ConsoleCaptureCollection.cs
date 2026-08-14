using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Serializes the ConsoleCapture tests, which redirect the process-global Console.Out and
///     Console.Error and assert on what they were restored to.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCaptureCollection
{
    public const string Name = "ConsoleCapture";
}
