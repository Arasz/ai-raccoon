using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     The `repair &lt;kind&gt; [--apply]` template: print the server-scanned report, and with --apply
///     commit a request the server applies — a thin client over <see cref="IRepairStore" /> that never
///     opens the bank from the CLI process. A kind supplies its report, report lines and applied message.
/// </summary>
public abstract class ReportThenApplyRepairCommands<TReport>(IRepairStore repair, RepairKind kind)
{
    public async Task<int> RunAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");

        var report = await ReportAsync(repair, cancellationToken);
        foreach (var line in Describe(report, apply))
        {
            await streams.WriteOutputLineAsync(line);
        }

        if (apply)
        {
            await repair.RequestRepairAsync(kind, cancellationToken);
            await streams.WriteOutputLineAsync(AppliedMessage);
        }

        return 0;
    }

    protected abstract Task<TReport> ReportAsync(IRepairStore repair, CancellationToken cancellationToken);

    protected abstract IEnumerable<string> Describe(TReport report, bool apply);

    protected abstract string AppliedMessage { get; }
}
