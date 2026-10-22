using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Storage;
using AiRaccoon.Tests.Integration.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;
using SearchResults = AiRaccoon.Core.Memory.SearchResults;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>Measures the application search path over a pinned copy with cached query embeddings.</summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ConcurrentSearchOverlapCollection.Name)]
public sealed class TypedSearchImpactProbe(ITestOutputHelper output)
{
    private sealed record Row
    {
        public long Id { get; init; }
        public string Hash { get; init; } = "";
        public string Path { get; init; } = "";
        public string Value { get; init; } = "";
        public string ProjectId { get; init; } = "";
        public string EntryType { get; init; } = "";
        public string Scope { get; init; } = "";
        public string? WorkspaceId { get; init; }
        public string? ContextLabel { get; init; }
        public bool HasEmbedding { get; init; }
        public byte[]? Embedding { get; init; }
    }

    private sealed record OracleHit(string Hash, double Cosine);
    private sealed record Sample(string Phase, int Wave, int Caller, double CallerMs, SearchTimings Timings,
        int ResultCount, bool ResultsMatch);
    private sealed record CaseResult(string Corpus, int Types, int EligibleRows, int UniqueValues,
        int ExpectedCount, int ActualCount, double ExactRecall, double TieAwareRecall, Sample[] Samples);

    [RetryFact]
    public async Task PinnedBank_ApplicationSearch_MatchesExhaustiveCosineAndRecordsCallerSamples()
    {
        var sourcePath = Environment.GetEnvironmentVariable("AIRACCOON_TYPED_SEARCH_SOURCE");
        if (sourcePath is null)
        {
            output.WriteLine("Set AIRACCOON_TYPED_SEARCH_SOURCE to an immutable pinned plaintext bank to run.");
            return;
        }
        var artifactPath = Environment.GetEnvironmentVariable("AIRACCOON_TYPED_SEARCH_REPORT")
            ?? Path.Combine(Path.GetTempPath(), "typed-search-impact.json");
        using var pin = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath + ".pin.json", TestContext.Current.CancellationToken));
        await using var source = File.OpenRead(sourcePath);
        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source, TestContext.Current.CancellationToken)).ToLowerInvariant();
        sourceHash.ShouldBe(pin.RootElement.GetProperty("sha256").GetString());
        var root = TestData.CreateTempRoot("typed-application-impact");
        try
        {
            var options = new InfrastructureOptions { DataRoot = root, Rid = "osx-arm64", Scope = InstallScope.User };
            File.Copy(sourcePath, SqliteConnectionFactory.BankPathFor(options));
            var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            var rebuildWatch = Stopwatch.StartNew();
            await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            var rebuildMs = rebuildWatch.Elapsed.TotalMilliseconds;
            var rows = (await connection.QueryAsync<Row>("""
                SELECT id, hash, path, value, project_id AS ProjectId, entry_type AS EntryType,
                       scope, workspace_id AS WorkspaceId, context_label AS ContextLabel,
                       embedding IS NOT NULL AS HasEmbedding, embedding
                FROM entries
                WHERE project_id IS NOT NULL
                """)).ToArray();
            var projectRows = rows.Where(row => row.Scope is "project" or "custom" && row.WorkspaceId is null).ToArray();
            var embeddedRows = projectRows.Where(row => row.HasEmbedding).ToArray();
            embeddedRows.Length.ShouldBeGreaterThan(0);
            var dimensions = embeddedRows.Select(row => row.Embedding!.Length / sizeof(float)).Distinct().Single();
            var providerVersion = await connection.ExecuteScalarAsync<string>("SELECT vec_version()");
            var beforeChunks = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM vec_entries_chunks");
            var structureChunks = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM vec_structure_chunks");
            await connection.ExecuteAsync("""
                INSERT INTO settings(key,value) VALUES('retrieval.structureAlpha','1.0') ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                INSERT INTO settings(key,value) VALUES('fusion.noRegression.enabled.global','false') ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """);
            var queryRows = embeddedRows.GroupBy(row => row.ProjectId).OrderByDescending(group => group.Count())
                .Take(2).Select(group => group.OrderBy(row => row.Hash, StringComparer.Ordinal).First()).ToArray();
            var vectors = queryRows.ToDictionary(row => "pinned-vector-" + row.Id, row => row.Embedding!, StringComparer.Ordinal);
            var embedder = Substitute.For<IEntryEmbedder>();
            embedder.EmbedQueryAsync(Arg.Any<SqliteConnection>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new QueryVector(vectors[call.Arg<string>()])));
            var store = SearchTimingsHarness.CreateStore(factory, TimeProvider.System, embedder);
            var cases = new List<CaseResult>();
            foreach (var queryRow in queryRows)
            {
                var queryVector = Floats(queryRow.Embedding!);
                foreach (var types in new[] { EntryType.Memory, EntryType.File, EntryType.Memory | EntryType.File, EntryTypeParameter.Default })
                {
                    var eligible = embeddedRows.Where(row => row.ProjectId == queryRow.ProjectId && Includes(types, row.EntryType)).ToArray();
                    var oracle = eligible.Select(row => new { Row = row, Cosine = Cosine(Floats(row.Embedding!), queryVector) })
                        .GroupBy(hit => hit.Row.Value, StringComparer.Ordinal)
                        .Select(group => group.OrderByDescending(hit => hit.Cosine).ThenBy(hit => hit.Row.Hash, StringComparer.Ordinal).First())
                        .OrderByDescending(hit => hit.Cosine).ThenBy(hit => hit.Row.Hash, StringComparer.Ordinal)
                        .Select(hit => new OracleHit(hit.Row.Hash, hit.Cosine)).ToArray();
                    var expected = oracle.Take(10).ToArray();
                    var query = new SearchQuery(queryRow.ProjectId, "pinned-vector-" + queryRow.Id, SearchScope.Project,
                        Limit: 10, MinRelativeScore: 0, FtsWeight: 0, VectorWeight: 1, SourceLambda: 0, EntryTypes: types);
                    SearchResults result = null!;
                    for (var warmup = 0; warmup < 3; warmup++)
                    {
                        result = await store.SearchAsync(query, TestContext.Current.CancellationToken);
                    }
                    var actual = result.Results.Select(row => row.Hash).ToArray();
                    actual.Length.ShouldBe(expected.Length);
                    var eligibleByHash = eligible.ToDictionary(row => row.Hash, StringComparer.Ordinal);
                    var cutoff = expected.Length == 0 ? double.PositiveInfinity : expected[^1].Cosine;
                    var valid = actual.Count(hash => eligibleByHash.TryGetValue(hash, out var row)
                        && Cosine(Floats(row.Embedding!), queryVector) >= cutoff - 0.00002);
                    valid.ShouldBe(expected.Length, JsonSerializer.Serialize(new
                    {
                        projectId = queryRow.ProjectId, types, expected,
                        actual = actual.Select(hash => new
                        {
                            hash,
                            row = rows.FirstOrDefault(row => row.Hash == hash),
                            cosine = eligibleByHash.TryGetValue(hash, out var row) ? Cosine(Floats(row.Embedding!), queryVector) : -99
                        }),
                        returned = result.Results.Select(row => new { row.Hash, row.ContentCosine, row.Ranking }),
                        evidence = result.EvidenceByHash?.Count
                    }));
                    var exactRecall = expected.Length == 0 ? 1 : (double)actual.Intersect(expected.Select(hit => hit.Hash)).Count() / expected.Length;
                    var samples = new List<Sample>();
                    for (var run = 0; run < 20; run++)
                    {
                        samples.Add(await MeasureAsync("serial", 0, run));
                    }
                    for (var wave = 0; wave < 3; wave++)
                    {
                        var waveId = wave;
                        var concurrent = await Task.WhenAll(Enumerable.Range(0, 20).Select(caller =>
                            Task.Run(() => MeasureAsync("concurrent20", waveId, caller), TestContext.Current.CancellationToken)));
                        samples.AddRange(concurrent);
                    }
                    var corpus = queryRow.ProjectId == "ai-raccoon" ? "corpus-1" : "corpus-2";
                    cases.Add(new CaseResult(corpus, (int)types, eligible.Length, oracle.Length, expected.Length, actual.Length,
                        exactRecall, expected.Length == 0 ? 1 : (double)valid / expected.Length, samples.ToArray()));

                    async Task<Sample> MeasureAsync(string phase, int wave, int caller)
                    {
                        var watch = Stopwatch.StartNew();
                        var measured = await store.SearchAsync(query, TestContext.Current.CancellationToken);
                        var milliseconds = watch.Elapsed.TotalMilliseconds;
                        var hashes = measured.Results.Select(row => row.Hash).ToArray();
                        hashes.ShouldBe(actual);
                        return new Sample(phase, wave, caller, milliseconds, measured.Timings, hashes.Length, true);
                    }
                }
            }
            await connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
            var databaseBytes = await connection.ExecuteScalarAsync<long>("SELECT page_count*page_size FROM pragma_page_count(),pragma_page_size()");
            var report = new
            {
                sourceLabel = "owner-private pinned bank", sourceHash, sourceBytes = source.Length, configuration = "Release", providerVersion, dimensions,
                copiedBankUpgradeMs = rebuildMs, databaseBytes, contentChunks = beforeChunks, structureChunks,
                rows = rows.Length, embeddedRows = embeddedRows.Length, entryTypes = rows.GroupBy(row => row.EntryType).ToDictionary(group => group.Key, group => group.Count()),
                search = new { alpha = 1, ftsWeight = 0, vectorWeight = 1, sourceLambda = 0, limit = 10, candidateWindow = 100 },
                limitations = "Cached pinned query vectors omit embedding inference and transport. Vector-only, project scope, alpha=1; typed hybrid/dual-vector/scope behavior is covered separately. Source upgrade timing includes opening and schema ensure; it is not isolated vector rebuild time.",
                cases
            };
            await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), TestContext.Current.CancellationToken);
            output.WriteLine(artifactPath);
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    private static bool Includes(EntryType types, string kind) => types == EntryTypeParameter.Default
        || kind == "memory" && (types & EntryType.Memory) != 0
        || kind == "file" && (types & EntryType.File) != 0;

    private static float[] Floats(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static double Cosine(float[] first, float[] second)
    {
        double dot = 0, firstNorm = 0, secondNorm = 0;
        for (var i = 0; i < first.Length; i++)
        {
            dot += (double)first[i] * second[i];
            firstNorm += (double)first[i] * first[i];
            secondNorm += (double)second[i] * second[i];
        }
        return dot / Math.Sqrt(firstNorm * secondNorm);
    }
}
