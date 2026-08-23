using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `repair reingest` handler: a thin client over <see cref="IRepairStore" />
///     — the report is scanned server-side and never opens the bank from the CLI process; --apply
///     commits a request the server applies, rather than writing here.
/// </summary>
public sealed class ReingestRepairCommands(IRepairStore repair)
{
    public async Task<int> RunAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");

        var report = await repair.ReportReingestAsync(cancellationToken);

        var verb = apply ? "queued for the server to reingest" : "would reingest (dry run; pass --apply to queue it)";
        await streams.WriteOutputLineAsync(
            $"reingest repair: {report.FilesToReingest} file(s) {verb}, {report.RowsAffected} row(s) affected, " +
            $"{report.ChunksToEmbed} chunk(s) to embed");
        if (report.RowsAffected > 0)
        {
            var lossVerb = apply ? "will be discarded for the" : "would be discarded for the";
            await streams.WriteOutputLineAsync(
                $"reingest repair: per-row metadata (rating, access_count, last_accessed_at) {lossVerb} " +
                $"{report.RowsAffected} affected row(s) — a re-chunk moves chunk boundaries, so hashes change " +
                "and there is no 1:1 row to carry it onto.");
        }

        if (apply)
        {
            await repair.RequestRepairAsync(RepairKind.Reingest, cancellationToken);
            await streams.WriteOutputLineAsync(
                "reingest repair: request committed; the server applies it and drains the resulting embeddings " +
                "on its next maintenance poll (~15s) — nothing left to run by hand.");
        }

        return 0;
    }
}
