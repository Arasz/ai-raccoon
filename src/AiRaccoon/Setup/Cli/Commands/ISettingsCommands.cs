using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>Settings-table verb handlers (access/model/retrieval/sweep) — thin, no state.</summary>
public interface ISettingsCommands
{
    Task<int> AccessDefaultSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);

    Task<int> AccessDefaultShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> AccessSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);

    Task<int> AccessUnsetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken);

    Task<int> AccessListAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> ModelSetLocalAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken);

    Task<int> ModelSetOpenAiAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);

    Task<int> ModelResetAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> ModelShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> RetrievalAlphaSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);

    Task<int> RetrievalAlphaShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);

    Task<int> SweepThresholdSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken);

    Task<int> SweepShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken);
}
