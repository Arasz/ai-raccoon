using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>Extract-config verb handlers (enable/mode/list); implemented by ExtractCommands.</summary>
public interface IExtractCommands
{
    Task<int> SetEnabledAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> SetModeAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken);

    Task<int> ListAsync(IMemoryStore store, TextWriter stdout, CancellationToken cancellationToken);
}
