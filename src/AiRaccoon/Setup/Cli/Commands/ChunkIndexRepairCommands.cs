using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>`repair chunk-index` handler: repositions chunk rows server-side (see <see cref="ReportThenApplyRepairCommands{TReport}" />).</summary>
public sealed class ChunkIndexRepairCommands(IRepairStore repair)
    : ReportThenApplyRepairCommands<ChunkIndexRepairReport>(repair, RepairKind.ChunkIndex)
{
    protected override string AppliedMessage =>
        "chunk-index repair: request committed; the server applies it on its next maintenance poll (~15s).";

    protected override Task<ChunkIndexRepairReport> ReportAsync(IRepairStore repair, CancellationToken cancellationToken) =>
        repair.ReportChunkIndexAsync(cancellationToken);

    protected override IEnumerable<string> Describe(ChunkIndexRepairReport report, bool apply)
    {
        var verb = apply ? "queued for the server to reposition" : "would reposition (dry run; pass --apply to queue it)";
        yield return $"chunk-index repair: {report.GroupsExamined} source group(s) examined, " +
                     $"{report.RowsRepositioned} row(s) {verb}, {report.RowsSetToUnknown} row(s) set to the unknown position (-1), " +
                     $"{report.RowsRetotalled} row(s) given their partition's row count as total_chunks";
    }
}
