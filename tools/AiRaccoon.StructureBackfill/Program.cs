using System.Globalization;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;

// Wave 6 corpus backfill: derives heading paths from stored chunk content, embeds them with
// the bundled ONNX model, and writes heading_path + structure_embedding + vec_structure rows.
// Idempotent — safe to re-run (the orchestrator re-runs it after the concurrent schema wave).
//
// Usage: AiRaccoon.StructureBackfill <memory.db path> [--alpha <0..1>]

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AiRaccoon.StructureBackfill <memory.db path> [--alpha <0..1>]");
    return 1;
}

var dbPath = args[0];
var alpha = StructureFusion.DefaultAlpha;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--alpha" && i + 1 < args.Length
        && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
    {
        alpha = parsed;
    }
}

Console.WriteLine($"Backfilling structure vectors into {dbPath} (alpha={alpha.ToString(CultureInfo.InvariantCulture)})");
var result = await new StructureBackfillService(new EmbeddingService()).RunAsync(dbPath, alpha);
Console.WriteLine(
    $"Backfill complete: {result.RowsProcessed} rows processed, {result.ChunksWithHeadings} with headings, " +
    $"{result.UniqueHeadingPaths} unique heading paths embedded, {result.VectorsWritten} structure vectors written.");
return 0;
