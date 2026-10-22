using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>Measures native partition layouts over an immutable pinned production backup.</summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class TypedVecLivePartitionProbe(ITestOutputHelper output)
{
    private sealed record VectorRow(long Id, string Ctx, string Type, byte[] Embedding);
    private sealed class Neighbor
    {
        public long Rowid { get; set; }
        public double Distance { get; set; }
    }

    [RetryFact]
    public async Task Pinned_vectors_preserve_typed_cosine_neighbors_across_native_layouts()
    {
        var sourcePath = Environment.GetEnvironmentVariable("AIRACCOON_TYPED_VEC_SOURCE");
        if (sourcePath is null)
        {
            output.WriteLine("Set AIRACCOON_TYPED_VEC_SOURCE to an immutable plaintext backup to run.");
            return;
        }

        var root = TestData.CreateTempRoot("typed-live");
        try
        {
            var bootstrap = new InfrastructureOptions { DataRoot = root, Rid = "osx-arm64", Scope = InstallScope.User };
            var bootstrapFactory = new SqliteConnectionFactory(bootstrap, NullKeyProvider.Resolver(bootstrap));
            await using (var initialized = await bootstrapFactory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                (await initialized.ExecuteScalarAsync<string>("SELECT vec_version()" )).ShouldBe("v0.1.9");
            }
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            await source.OpenAsync(TestContext.Current.CancellationToken);
            await source.ExecuteAsync("PRAGMA query_only=ON");
            var pin = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath + ".pin.json", TestContext.Current.CancellationToken));
            await using var stream = File.OpenRead(sourcePath);
            var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken)).ToLowerInvariant();
            sourceHash.ShouldBe(pin.RootElement.GetProperty("sha256").GetString());
            var report = new List<object>();
            foreach (var modality in new[] { "content", "structure" })
            {
                var column = modality == "content" ? "embedding" : "structure_embedding";
                var rows = (await source.QueryAsync<VectorRow>($"SELECT id, {MemorySql.ContextKeyExpression("")} AS Ctx, CASE WHEN source_file IS NOT NULL AND source_file <> '' AND path=source_file THEN 'file' ELSE 'memory' END AS Type, {column} AS Embedding FROM entries WHERE {column} IS NOT NULL")).ToArray();
                rows.Length.ShouldBeGreaterThan(0);
                var dimensions = rows[0].Embedding.Length / sizeof(float);
                rows.ShouldAllBe(r => r.Embedding.Length == dimensions * sizeof(float));
                var grouped = rows.GroupBy(r => r.Ctx).OrderByDescending(g => g.Count()).ToArray();
                var contexts = grouped.Take(3).Concat(grouped.TakeLast(1)).Select(g => g.Key).Distinct().ToArray();
                foreach (var layout in new[] { "baseline", "composite", "native" })
                {
                    var dataRoot = Path.Combine(root, modality + "-" + layout);
                    var options = new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
                    var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
                    await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
                    var ddl = layout == "native" ? "ctx TEXT partition key,type TEXT partition key" : "ctx TEXT partition key";
                    await connection.ExecuteAsync($"CREATE VIRTUAL TABLE measured USING vec0({ddl},embedding float[{dimensions}] distance_metric=cosine)");
                    var watch = Stopwatch.StartNew();
                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var row in rows)
                        {
                            var ctx = layout == "composite" ? row.Ctx + "|" + row.Type : row.Ctx;
                            await connection.ExecuteAsync(layout == "native"
                                ? "INSERT INTO measured(rowid,ctx,type,embedding) VALUES(@Id,@ctx,@Type,@Embedding)"
                                : "INSERT INTO measured(rowid,ctx,embedding) VALUES(@Id,@ctx,@Embedding)", new { row.Id, ctx, row.Type, row.Embedding }, transaction);
                        }
                        transaction.Commit();
                    }
                    var buildMs = watch.Elapsed.TotalMilliseconds;
                    await connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
                    var chunks = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM measured_chunks");
                    var capacity = await connection.ExecuteScalarAsync<long>("SELECT max(size) FROM measured_chunks");
                    var databaseBytes = await connection.ExecuteScalarAsync<long>("SELECT page_count * page_size FROM pragma_page_count(),pragma_page_size()");
                    var samples = new List<object>();
                    var successes = 0;
                    foreach (var ctx in contexts)
                    {
                        var queryRow = rows.First(r => r.Ctx == ctx);
                        var query = Floats(queryRow.Embedding);
                        var distances = rows.Where(r => r.Ctx == ctx).ToDictionary(r => r.Id, r => Cosine(Floats(r.Embedding), query));
                        foreach (var type in new string?[] { null, "memory", "file" })
                        {
                            var eligible = rows.Where(r => r.Ctx == ctx && (type is null || r.Type == type)).ToArray();
                            var expected = eligible.OrderBy(r => distances[r.Id]).Take(10).Select(r => r.Id).ToArray();
                            var predicate = layout switch
                            {
                                "composite" when type is null => "ctx IN (@memory,@file)",
                                "composite" => "ctx=@typed",
                                "native" when type is not null => "ctx=@ctx AND type=@type",
                                _ => "ctx=@ctx"
                            };
                            var inner = $"SELECT rowid,distance FROM measured WHERE {predicate} AND embedding MATCH @vec AND k=10";
                            var sql = layout == "composite" && type is null ? $"SELECT rowid,distance FROM ({inner}) ORDER BY distance+0 LIMIT 10" : inner + " ORDER BY distance";
                            var args = new { ctx, type, memory = ctx + "|memory", file = ctx + "|file", typed = ctx + "|" + type, vec = queryRow.Embedding };
                            var neighbors = (await connection.QueryAsync<Neighbor>(sql, args)).ToArray();
                            if (layout == "baseline" && type is not null)
                            {
                                var allowed = eligible.Select(r => r.Id).ToHashSet();
                                neighbors = neighbors.Where(n => allowed.Contains(n.Rowid)).ToArray();
                            }
                            var countCorrect = neighbors.Length == expected.Length;
                            if (layout != "baseline" || type is null)
                            {
                                countCorrect.ShouldBeTrue();
                                var allowed = eligible.Select(r => r.Id).ToHashSet();
                                foreach (var neighbor in neighbors)
                                {
                                    allowed.ShouldContain(neighbor.Rowid);
                                    Math.Abs(neighbor.Distance - distances[neighbor.Rowid]).ShouldBeLessThan(0.00002);
                                    distances[neighbor.Rowid].ShouldBeLessThanOrEqualTo(expected.Length == 0 ? 0 : distances[expected[^1]] + 0.00002);
                                }
                                neighbors.Select(n => n.Rowid).Distinct().Count().ShouldBe(expected.Length);
                            }
                            for (var warmup = 0; warmup < 3; warmup++)
                            {
                                await connection.QueryAsync<Neighbor>(sql, args);
                            }
                            var serial = new List<double>();
                            for (var run = 0; run < 20; run++)
                            {
                                watch.Restart();
                                await connection.QueryAsync<Neighbor>(sql, args);
                                serial.Add(watch.Elapsed.TotalMilliseconds);
                            }
                            var concurrent = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
                            {
                                var caller = Stopwatch.StartNew();
                                await using var reader = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
                                var sqlWatch = Stopwatch.StartNew();
                                var result = (await reader.QueryAsync<Neighbor>(sql, args)).ToArray();
                                var sqlMs = sqlWatch.Elapsed.TotalMilliseconds;
                                return new { sqlMs, callerMs = caller.Elapsed.TotalMilliseconds, count = result.Length };
                            })));
                            successes++;
                            samples.Add(new { ctx, type, sql, queryId = queryRow.Id, eligible = eligible.Length, expected, actual = neighbors, countCorrect, serial, concurrent });
                        }
                    }
                    successes.ShouldBe(contexts.Length * 3);
                    report.Add(new { modality, layout, dimensions, count = rows.Length, distribution = rows.GroupBy(r => new { r.Ctx, r.Type }).Select(g => new { g.Key, count = g.Count() }), buildMs, chunks, capacity, vectorBytes = chunks * capacity * dimensions * sizeof(float), databaseBytes, samples });
                }
            }
            var artifact = JsonSerializer.Serialize(new { sourceHash, pin = pin.RootElement, build = "Release", sqlite = await source.ExecuteScalarAsync<string>("SELECT sqlite_version()"), vec = "v0.1.9", report }, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(artifact);
            var artifactPath = Environment.GetEnvironmentVariable("AIRACCOON_TYPED_VEC_LIVE_ARTIFACT");
            if (artifactPath is not null)
            {
                await File.WriteAllTextAsync(artifactPath, artifact, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    private static float[] Floats(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }
        return 1 - dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
