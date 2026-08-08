using System.Text.Json;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>Golden-file hit: the committed reference oracle output for one ranked position.</summary>
public sealed record GoldenHit(string Hash, double Ranking, string Path, string Snippet);

/// <summary>Golden-file query entry: the reference top-k for one graded query.</summary>
public sealed record GoldenQuery(string Id, string Text, IReadOnlyList<GoldenHit> Hits);

/// <summary>
///     Committed golden reference output (assets/reference-topk.json): deterministic top-k
///     from the pinned extension over the shared corpus, checked against fused-retriever
///     rankings by the parity gate.
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

    /// <summary>Ranking tolerance for cross-platform comparisons; see ADR-0015.</summary>
    public const double RankingTolerance = 5e-3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Path { get; init; } = "";

    public static GoldenFile FromRun(ReferenceRun run) =>
        new(
            CurrentSchemaVersion,
            run.Engine,
            run.Model,
            ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.ModelFileName).Sha256,
            run.Context,
            run.K,
            run.MinRanking,
            run.DocumentCount,
            [
                .. run.ResultsByQuery.Select(q => new GoldenQuery(q.QueryId, q.QueryText,
                    [.. q.Hits.Select(h => new GoldenHit(h.Hash, Math.Round(h.Ranking, RankingPrecision), h.Path, h.Snippet))]))
            ]);

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

    /// <summary>
    ///     Portable comparison against a fresh reference run (ADR-0015): hashes compared as sets,
    ///     not positions, with matched rankings required to agree within <see cref="RankingTolerance"/>.
    ///     Order isn't asserted separately since set equality plus the tolerance already constrain it.
    /// </summary>
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

            differences.AddRange(QueryDifferences(queryId, expectedQuery.Hits, actualQuery.Hits));
        }

        foreach (var queryId in actual.Keys.Where(id => !expected.ContainsKey(id)))
        {
            differences.Add($"{queryId}: present in fresh run but not in golden");
        }

        return differences;
    }

    private static IEnumerable<string> QueryDifferences(
        string queryId, IReadOnlyList<GoldenHit> expectedHits, IReadOnlyList<ReferenceHit> actualHits)
    {
        // The k-th (last) golden ranking is the cut point: a hash within tolerance of it that
        // only appears on one side is a boundary substitution, not a regression — absorb it.
        var boundaryRanking = expectedHits[^1].Ranking;
        var expectedByHash = expectedHits.ToDictionary(h => h.Hash, StringComparer.Ordinal);
        var actualByHash = actualHits.ToDictionary(h => h.Hash, StringComparer.Ordinal);

        foreach (var (hash, e) in expectedByHash)
        {
            if (!actualByHash.TryGetValue(hash, out var a))
            {
                if (Math.Abs(e.Ranking - boundaryRanking) > RankingTolerance)
                {
                    yield return $"{queryId}: {hash} in golden (ranking {e.Ranking:F6}) missing from fresh run";
                }

                continue;
            }

            if (e.Path != a.Path)
            {
                yield return $"{queryId}: {hash} path expected {e.Path}, got {a.Path}";
            }

            if (Math.Abs(e.Ranking - a.Ranking) > RankingTolerance)
            {
                yield return $"{queryId}: {hash} ranking expected {e.Ranking:F6}, got {a.Ranking:F6}";
            }
        }

        foreach (var (hash, a) in actualByHash)
        {
            if (expectedByHash.ContainsKey(hash))
            {
                continue;
            }

            if (Math.Abs(a.Ranking - boundaryRanking) > RankingTolerance)
            {
                yield return $"{queryId}: {hash} in fresh run (ranking {a.Ranking:F6}) not in golden";
            }
        }
    }
}
