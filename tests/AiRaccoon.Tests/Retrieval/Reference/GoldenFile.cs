using System.Text.Json;
using AiRaccoon.Tests.Retrieval.Assets;

namespace AiRaccoon.Tests.Retrieval.Reference;

/// <summary>Golden-file hit: the committed reference oracle output for one ranked position.</summary>
public sealed record GoldenHit(string Hash, double Ranking, string Path, string Snippet);

/// <summary>Golden-file query entry: the reference top-k for one graded query.</summary>
public sealed record GoldenQuery(string Id, string Text, IReadOnlyList<GoldenHit> Hits);

/// <summary>
///     The committed golden reference output (assets/reference-topk.json): deterministic top-k
///     from the pinned extension over the shared corpus. The P6 parity gate compares the fused
///     retriever's rankings against this file.
/// </summary>
public sealed record GoldenFile(
    int SchemaVersion,
    string Engine,
    string Model,
    string ModelSha256,
    string Context,
    int K,
    double MinRanking,
    int DocumentCount,
    IReadOnlyList<GoldenQuery> Queries)
{
    public const string FileName = "reference-topk.json";
    public const int CurrentSchemaVersion = 1;
    public const int RankingPrecision = 6;

    public string Path { get; init; } = "";

    public static GoldenFile FromRun(ReferenceRun run) => new(
        CurrentSchemaVersion,
        run.Engine,
        run.Model,
        ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.ModelFileName).Sha256,
        run.Context,
        run.K,
        run.MinRanking,
        run.DocumentCount,
        [.. run.ResultsByQuery.Select(q => new GoldenQuery(q.QueryId, q.QueryText,
            [.. q.Hits.Select(h => new GoldenHit(h.Hash, Math.Round(h.Ranking, RankingPrecision), h.Path, h.Snippet))]))]);

    public static GoldenFile Load(string path)
    {
        var golden = JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException($"golden file {path} is empty.");
        return golden with { Path = path };
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Exact order + near-exact ranking comparison against a fresh reference run.</summary>
    public IReadOnlyList<string> Differences(ReferenceRun run)
    {
        var differences = new List<string>();

        if (run.Engine != Engine)
        {
            differences.Add($"engine: expected {Engine}, got {run.Engine}");
        }

        if (run.Model != Model)
        {
            differences.Add($"model: expected {Model}, got {run.Model}");
        }

        var expected = Queries.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var actual = run.ResultsByQuery.ToDictionary(q => q.QueryId, StringComparer.Ordinal);

        foreach (var (queryId, expectedQuery) in expected)
        {
            if (!actual.TryGetValue(queryId, out var actualQuery))
            {
                differences.Add($"{queryId}: missing from fresh run");
                continue;
            }

            var expectedHits = expectedQuery.Hits;
            var actualHits = actualQuery.Hits;
            var compared = Math.Min(expectedHits.Count, actualHits.Count);
            for (var i = 0; i < compared; i++)
            {
                var e = expectedHits[i];
                var a = actualHits[i];
                if (e.Hash != a.Hash || e.Path != a.Path ||
                    Math.Abs(e.Ranking - a.Ranking) > 1e-6)
                {
                    differences.Add(
                        $"{queryId}[{i}]: expected ({e.Hash}, {e.Ranking:F6}, {e.Path}), got ({a.Hash}, {a.Ranking:F6}, {a.Path})");
                }
            }

            if (expectedHits.Count != actualHits.Count)
            {
                differences.Add($"{queryId}: hit count {expectedHits.Count} vs {actualHits.Count}");
            }
        }

        foreach (var queryId in actual.Keys.Where(id => !expected.ContainsKey(id)))
        {
            differences.Add($"{queryId}: present in fresh run but not in golden");
        }

        return differences;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
