using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>Bank-maintenance verb handlers (interval/vacuum-interval/list); implemented by MaintenanceCommands.</summary>
public interface IMaintenanceCommands
{
    Task<int> SetCheckpointIntervalAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
        TextWriter stderr, CancellationToken cancellationToken);

    Task<int> SetVacuumIntervalAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
        TextWriter stderr, CancellationToken cancellationToken);

    Task<int> ListAsync(IMemoryStore store, TextWriter stdout, CancellationToken cancellationToken);
}
