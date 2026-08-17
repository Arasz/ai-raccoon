using System.CommandLine;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `repair chunk-index` handler (GH #371): explicit-only re-derivation of chunk_index/total_chunks
///     from each row's source_file. Dry run reports by default; --apply writes. Deliberately its own
///     top-level operation, not a maintenance-job subcommand — it must never be reachable from the
///     job list <see cref="AiRaccoon.Infrastructure.Maintenance.BankMaintenanceHostedService" /> runs on bank open.
/// </summary>
public sealed class ChunkIndexRepairCommands(ISqliteConnectionFactory bankConnectionFactory, IFileTypeMatcher fileTypeMatcher)
{
    public async Task<int> RunAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");

        await using var connection = await bankConnectionFactory.OpenBankAsync(cancellationToken);
        var report = await new ChunkIndexRepair(fileTypeMatcher).RunAsync(connection, apply, cancellationToken);

        var verb = apply ? "repositioned" : "would reposition (dry run; pass --apply to write)";
        await streams.WriteOutputLineAsync($"chunk-index repair: {report.GroupsExamined} source group(s) examined, " +
                                            $"{report.RowsRepositioned} row(s) {verb}, {report.RowsSetToUnknown} row(s) set to the unknown position (-1)");
        return 0;
    }
}
