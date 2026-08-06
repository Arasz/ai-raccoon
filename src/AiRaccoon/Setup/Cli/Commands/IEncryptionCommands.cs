using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

public interface IEncryptionCommands
{
    Task<int> BitwardenAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        CancellationToken cancellationToken);

    Task<int> ShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> UnsetAsync(IMemoryStore store, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken);
}
