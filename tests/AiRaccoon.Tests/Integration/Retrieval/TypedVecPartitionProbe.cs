using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>Opt-in characterization of typed native KNN layouts against an exhaustive cosine oracle.</summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class TypedVecPartitionProbe(ITestOutputHelper output)
{
    private sealed record Neighbor(long Rowid, double Distance);

    private sealed record Row(long Id, string Ctx, string Type, float[] Vector);

    [RetryFact]
    public async Task Typed_partitions_preserve_minority_and_unrestricted_nearest_neighbors()
    {
        if (Environment.GetEnvironmentVariable("AIRACCOON_TYPED_VEC_PROBE") is null)
        {
            output.WriteLine("Set AIRACCOON_TYPED_VEC_PROBE=1 to execute the native layout experiment.");
            return;
        }

        const int Dimensions = 384;
        const int K = 10;
        var root = TestData.CreateTempRoot("typed-native");
        try
        {
            var options = new InfrastructureOptions { DataRoot = root, Rid = "osx-arm64", Scope = InstallScope.User };
            var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            var random = new Random(2197);
            var rows = Enumerable.Range(1, 3000).Select(id => new Row(id,
                id <= 65 ? $"sparse:{(id - 1) / 5}" : $"dense:{id % 7}",
                id % 100 == 0 ? "file" : "memory",
                Enumerable.Range(0, Dimensions).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray())).ToArray();
            var results = new List<object>();
            foreach (var layout in new[] { "baseline", "composite", "native" })
            {
                var ddl = layout == "native" ? "ctx TEXT partition key, type TEXT partition key" : "ctx TEXT partition key";
                await connection.ExecuteAsync($"CREATE VIRTUAL TABLE probe_{layout} USING vec0({ddl}, embedding float[{Dimensions}] distance_metric=cosine)");
                var watch = Stopwatch.StartNew();
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var row in rows)
                    {
                        var ctx = layout == "composite" ? row.Ctx + "|" + row.Type : row.Ctx;
                        var columns = layout == "native" ? "rowid,ctx,type,embedding" : "rowid,ctx,embedding";
                        var values = layout == "native" ? "@Id,@ctx,@Type,@vec" : "@Id,@ctx,@vec";
                        await connection.ExecuteAsync($"INSERT INTO probe_{layout}({columns}) VALUES ({values})",
                            new { row.Id, ctx, row.Type, vec = Bytes(row.Vector) }, transaction);
                    }
                    transaction.Commit();
                }
                var buildMs = watch.Elapsed.TotalMilliseconds;
                var chunks = await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM probe_{layout}_chunks");
                var capacity = await connection.ExecuteScalarAsync<long>($"SELECT max(size) FROM probe_{layout}_chunks");
                var samples = new List<object>();
                var successfulQueries = 0;
                foreach (var modality in new[] { "content", "structure" })
                {
                    var query = rows[modality == "content" ? 99 : 199].Vector;
                    foreach (var ctx in new[] { "dense:0", "sparse:0" })
                    {
                        foreach (var type in new string?[] { null, "file", "memory", "both" })
                        {
                            var expected = rows.Where(r => r.Ctx == ctx && (type is null or "both" || r.Type == type))
                                .OrderBy(r => Cosine(r.Vector, query)).Take(K).Select(r => r.Id).ToArray();
                            var predicate = layout switch
                            {
                                "composite" when type is null or "both" => "ctx IN (@memory,@file)",
                                "composite" => "ctx=@typed",
                                "native" when type == "both" => "ctx=@ctx AND type IN ('memory','file')",
                                "native" when type is not null => "ctx=@ctx AND type=@type",
                                _ => "ctx=@ctx"
                            };
                            var sql = $"SELECT rowid,distance FROM (SELECT rowid,distance FROM probe_{layout} WHERE {predicate} AND embedding MATCH @vec AND k={K}) ORDER BY distance + 0 LIMIT {K}";
                            if (layout != "composite" && type != "both")
                            {
                                sql = $"SELECT rowid,distance FROM probe_{layout} WHERE {predicate} AND embedding MATCH @vec AND k={K} ORDER BY distance";
                            }
                            var args = new { ctx, type, memory = ctx + "|memory", file = ctx + "|file", typed = ctx + "|" + type, vec = Bytes(query) };
                            try
                            {
                                var neighbors = (await connection.QueryAsync<Neighbor>(sql, args)).ToArray();
                                var actual = neighbors.Select(n => n.Rowid).ToArray();
                                if (layout == "baseline" && type == "file")
                                {
                                    actual = actual.Where(id => rows[id - 1].Type == type).ToArray();
                                }
                                var correct = expected.SequenceEqual(actual);
                                if (Environment.GetEnvironmentVariable("AIRACCOON_TYPED_VEC_RED") is not null || layout != "baseline")
                                {
                                    actual.ShouldBe(expected, $"{layout}/{modality}/{ctx}/{type ?? "all"}");
                                    foreach (var neighbor in neighbors)
                                    {
                                        Math.Abs(neighbor.Distance - Cosine(rows[neighbor.Rowid - 1].Vector, query)).ShouldBeLessThan(0.00001);
                                    }
                                }
                                for (var warmup = 0; warmup < 3; warmup++)
                                {
                                    await connection.QueryAsync<long>(sql, args);
                                }
                                var timings = new List<double>();
                                for (var run = 0; run < 20; run++)
                                {
                                    watch.Restart();
                                    await connection.QueryAsync<long>(sql, args);
                                    timings.Add(watch.Elapsed.TotalMilliseconds);
                                }
                                successfulQueries++;
                                samples.Add(new { modality, ctx, type, sql, expected, actual, distances = neighbors.Select(n => n.Distance), correct, timings });
                            }
                            catch (Microsoft.Data.Sqlite.SqliteException exception)
                            {
                                samples.Add(new { modality, ctx, type, error = exception.Message });
                            }
                        }
                    }
                }
                successfulQueries.ShouldBe(16, $"{layout} must execute every query, not silently record SQL errors");
                var tiedVector = new float[Dimensions];
                tiedVector[0] = 1;
                for (var tie = 1; tie <= 15; tie++)
                {
                    var tieType = tie % 2 == 0 ? "file" : "memory";
                    var tieCtx = layout == "composite" ? "ties|" + tieType : "ties";
                    var columns = layout == "native" ? "rowid,ctx,type,embedding" : "rowid,ctx,embedding";
                    var values = layout == "native" ? "@id,@ctx,@type,@vec" : "@id,@ctx,@vec";
                    await connection.ExecuteAsync($"INSERT INTO probe_{layout}({columns}) VALUES ({values})",
                        new { id = 10000 + tie, ctx = tieCtx, type = tieType, vec = Bytes(tiedVector) });
                }
                var tiePredicate = layout == "composite" ? "ctx IN ('ties|memory','ties|file')" : "ctx='ties'";
                var tieSql = $"SELECT rowid,distance FROM (SELECT rowid,distance FROM probe_{layout} WHERE {tiePredicate} AND embedding MATCH @vec AND k={K}) ORDER BY distance+0 LIMIT {K}";
                var ties = (await connection.QueryAsync<Neighbor>(tieSql, new { vec = Bytes(tiedVector) })).ToArray();
                ties.Length.ShouldBe(K);
                ties.Select(t => t.Rowid).Distinct().Count().ShouldBe(K);
                foreach (var tie in ties)
                {
                    tie.Rowid.ShouldBeInRange(10001, 10015);
                    Math.Abs(tie.Distance).ShouldBeLessThan(0.00001);
                }
                results.Add(new { layout, buildMs, chunks, capacity, vectorBytes = chunks * capacity * Dimensions * sizeof(float), samples });
            }
            var artifact = JsonSerializer.Serialize(new
            {
                provider = connection.GetType().FullName,
                sqlite = await connection.ExecuteScalarAsync<string>("SELECT sqlite_version()"),
                vec = await connection.ExecuteScalarAsync<string>("SELECT vec_version()"),
                corpusSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows))),
                dimensions = Dimensions, seed = 2197, count = rows.Length,
                distribution = rows.GroupBy(r => new { r.Ctx, r.Type }).Select(g => new { g.Key, count = g.Count() }), results
            }, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(artifact);
            var artifactPath = Environment.GetEnvironmentVariable("AIRACCOON_TYPED_VEC_ARTIFACT");
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

    private static byte[] Bytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
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
