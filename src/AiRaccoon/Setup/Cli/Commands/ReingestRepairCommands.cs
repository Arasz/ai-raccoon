using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>`repair reingest` handler: re-chunks and re-embeds files server-side (see <see cref="ReportThenApplyRepairCommands{TReport}" />).</summary>
public sealed class ReingestRepairCommands(IRepairStore repair)
    : ReportThenApplyRepairCommands<ReingestRepairReport>(repair, RepairKind.Reingest)
{
    protected override string AppliedMessage =>
        "reingest repair: request committed; the server applies it and drains the resulting embeddings " +
        "on its next maintenance poll (~15s) — nothing left to run by hand.";

    protected override Task<ReingestRepairReport> ReportAsync(IRepairStore repair, CancellationToken cancellationToken) =>
        repair.ReportReingestAsync(cancellationToken);

    protected override IEnumerable<string> Describe(ReingestRepairReport report, bool apply)
    {
        var verb = apply ? "queued for the server to reingest" : "would reingest (dry run; pass --apply to queue it)";
        yield return $"reingest repair: {report.FilesToReingest} file(s) {verb}, {report.RowsAffected} row(s) affected, " +
                     $"{report.ChunksToEmbed} chunk(s) to embed";
        if (report.RowsAffected > 0)
        {
            var lossVerb = apply ? "will be discarded for the" : "would be discarded for the";
            yield return $"reingest repair: per-row metadata (rating, access_count, last_accessed_at) {lossVerb} " +
                         $"{report.RowsAffected} affected row(s) — a re-chunk moves chunk boundaries, so hashes change " +
                         "and there is no 1:1 row to carry it onto.";
        }
    }
}
