using System.CommandLine;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `repair reingest` handler: re-ingests source files a chunker change left with rows
///     <c>ChunkIndexRepair</c> could only mark chunk_index = -1 for. Dry run reports by default;
///     --apply routes each file through <see cref="IMemoryStore.ReplaceAsync" />. Its own top-level
///     operation, not a maintenance-job subcommand — never reachable from the job list a bank open
///     runs (same explicit-only constraint as `repair chunk-index`).
/// </summary>
public sealed class ReingestRepairCommands(
    ISqliteConnectionFactory bankConnectionFactory, IFileTypeMatcher fileTypeMatcher, IMemoryStore store)
{
    public async Task<int> RunAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");

        await using var connection = await bankConnectionFactory.OpenBankAsync(cancellationToken);
        var report = await new ReingestRepair(new ChunkPositionScanner(fileTypeMatcher))
            .RunAsync(connection, store, apply, cancellationToken);

        var verb = apply ? "reingested" : "would reingest (dry run; pass --apply to write)";
        await streams.WriteOutputLineAsync(
            $"reingest repair: {report.FilesToReingest} file(s) {verb}, {report.RowsAffected} row(s) affected, " +
            $"{report.ChunksToEmbed} chunk(s) to embed");
        if (report.RowsAffected > 0)
        {
            var lossVerb = apply ? "was discarded for the" : "will be discarded for the";
            await streams.WriteOutputLineAsync(
                $"reingest repair: per-row metadata (rating, access_count, last_accessed_at) {lossVerb} " +
                $"{report.RowsAffected} affected row(s) — a re-chunk moves chunk boundaries, so hashes change " +
                "and there is no 1:1 row to carry it onto.");
        }

        return 0;
    }
}
