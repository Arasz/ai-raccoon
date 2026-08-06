using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>Cloud-sync verb handlers (add s3/azure, remove, show) — interactive stdin secrets, no state.</summary>
public interface ISyncCommands
{
    Task<int> AddS3Async(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        CancellationToken cancellationToken);

    Task<int> AddAzureAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        CancellationToken cancellationToken);

    Task<int> RemoveAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> ShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);
}
