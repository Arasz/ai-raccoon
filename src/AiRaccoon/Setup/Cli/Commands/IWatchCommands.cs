using System.CommandLine;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>Watch-config verb handlers (enable/disable, scope, concurrency, list, registered, remove).</summary>
public interface IWatchCommands
{
    Task<int> SetEnabledAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);

    Task<int> ScopeAddAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken);

    Task<int> ScopeRemoveAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken);

    Task<int> ScopeListAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken);

    Task<int> ConcurrencyAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);

    Task<int> ListAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> RegisteredAsync(ParseResult parseResult, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> RemoveAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken);
}
