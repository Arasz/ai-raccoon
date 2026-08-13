using System.CommandLine;
using System.CommandLine.Parsing;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Setup.Cli.Options;

namespace AiRaccoon.Setup.Cli.Commands;

public sealed class ServeCommands(INodeRunner nodeRunner, IObservabilityRunner observabilityRunner)
{
    public Task<int> Observability(CliInput cliInput, StandardStreams streams,
        CancellationToken ctx) =>
        observabilityRunner.RunAsync(cliInput, streams, ctx);

    public Task<int> StartNode(CliInput cliInput, StandardStreams streams,
        CancellationToken ctx) =>
        nodeRunner.RunAsync(cliInput, streams, ctx);
}

public static class ServeCommandOptionsReader
{
    extension(ParseResult result)
    {
        public OptionReadResult<ServeCliOptions> GetServeOptions() =>
            result.ReadOptions((parseResult, collectedErrors) => new ServeCliOptions
            {
                Node = new NodeCliOptions
                {
                    Port = ResolvePort(parseResult, collectedErrors),
                    Format = parseResult.ReadOption(CliCommandTree.ServeFormatOption, "", collectedErrors),
                    IdleTimeout = parseResult.ReadOption(CliCommandTree.ServeIdleTimeoutOption, "", collectedErrors),
                    McpEntry = parseResult.ReadOption(CliCommandTree.ServeMcpEntryOption, false, collectedErrors, false),
                    Restart = parseResult.ReadOption(CliCommandTree.ServeRestartOption, false, collectedErrors, false)
                },
                Observability = new ObservabilityCliOptions
                {
                    Port = parseResult.ReadOption(CliCommandTree.ObservabilityPortOption, DefaultOptions.Port, collectedErrors)
                }
            });
    }

    /// <summary>Serve's own --port wins; else the root --port; else 7721 (docs/plans/2026-08-06-http-serve-mode-plan.md R7/R12).</summary>
    private static int ResolvePort(ParseResult parseResult, List<string> collectedErrors)
    {
        if (parseResult.GetResult(CliCommandTree.ServePortOption) is OptionResult { Tokens.Count: > 0 })
        {
            return parseResult.ReadOption(CliCommandTree.ServePortOption, DefaultOptions.Port, collectedErrors);
        }

        if (parseResult.GetResult(CliCommandTree.LaunchPortOption) is OptionResult { Tokens.Count: > 0 })
        {
            return parseResult.ReadOption(CliCommandTree.LaunchPortOption, DefaultOptions.Port, collectedErrors);
        }

        return DefaultOptions.Port;
    }
}

public sealed record NodeCliOptions
{
    public required int Port { get; init; }
    public required string Format { get; set; }
    public required string IdleTimeout { get; set; }
    public required bool McpEntry { get; set; }
    public required bool Restart { get; set; }
}

public sealed record ObservabilityCliOptions
{
    public required int Port { get; init; }
}

public sealed record ServeCliOptions
{
    public required NodeCliOptions Node { get; init; }
    public required ObservabilityCliOptions Observability { get; set; }
}
