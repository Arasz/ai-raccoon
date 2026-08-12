using AiRaccoon.Setup.Cli;

namespace AiRaccoon.Hosting.Node;

public interface INodeRunner
{
    Task<int> RunAsync(CliInput cliInput, StandardStreams streams, CancellationToken ctx);
}
