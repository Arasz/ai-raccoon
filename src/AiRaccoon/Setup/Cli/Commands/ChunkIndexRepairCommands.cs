using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `repair chunk-index` handler: a thin client over <see cref="IRepairStore" />
///     — the report is scanned server-side and never opens the bank from the
///     CLI process; --apply commits a request the server applies, rather than writing here.
/// </summary>
public sealed class ChunkIndexRepairCommands(IRepairStore repair)
{
    public async Task<int> RunAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");

        var report = await repair.ReportChunkIndexAsync(cancellationToken);

        var verb = apply ? "queued for the server to reposition" : "would reposition (dry run; pass --apply to queue it)";
        await streams.WriteOutputLineAsync($"chunk-index repair: {report.GroupsExamined} source group(s) examined, " +
                                           $"{report.RowsRepositioned} row(s) {verb}, {report.RowsSetToUnknown} row(s) set to the unknown position (-1)");

        if (apply)
        {
            await repair.RequestRepairAsync(RepairKind.ChunkIndex, cancellationToken);
            await streams.WriteOutputLineAsync(
                "chunk-index repair: request committed; the server applies it on its next maintenance poll (~15s).");
        }

        return 0;
    }
}
