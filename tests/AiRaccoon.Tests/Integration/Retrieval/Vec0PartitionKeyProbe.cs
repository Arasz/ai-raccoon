using System.Diagnostics;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     WP5's blocking measurement. The package cannot choose between dropping the `ctx` partition key
///     and coarsening it "by the size number alone" — its own acceptance criteria require a measured
///     latency comparison, because partitioning exists to prune before the `MATCH`.
///     <para>
///         Skipped unless <see cref="RunEnvVar" /> is set: it builds three copies of the corpus's
///         vectors and times KNN over each, which is a minute of work and asserts nothing about the
///         product. Run it, read the numbers, record them in the plan.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class Vec0PartitionKeyProbe : IDisposable
{
    internal const string RunEnvVar = "AIRACCOON_VEC0_PARTITION_PROBE";

    private const int Warmups = 3;
    private const int Runs = 40;
    private const int K = 10;

    private readonly string _dataRoot = TestData.CreateTempRoot("vec0-partition-probe");
    private readonly ITestOutputHelper _output;

    public Vec0PartitionKeyProbe(ITestOutputHelper output) => _output = output;

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task Probe_ComparesChunkBytesAndKnnLatency_AcrossPartitionShapes()
    {
        if (Environment.GetEnvironmentVariable(RunEnvVar) is null)
        {
            _output.WriteLine(
                $"{RunEnvVar} not set — this probe measures vec0 chunk allocation and KNN latency for "
                + "three partition shapes (WP5). It asserts nothing about the product; set the variable to run it.");
            return;
        }

        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db"), dbPath);

        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        List<(long Id, string Ctx, string Scope, byte[] Embedding)> vectors = (await connection.QueryAsync<(long Id, string Ctx, string Scope, byte[] Embedding)>(
                """
                SELECT e.id AS Id,
                       COALESCE(e.context_label, e.scope || ':' || e.project_id) AS Ctx,
                       e.scope AS Scope,
                       e.embedding AS Embedding
                FROM entries e
                WHERE e.embedding IS NOT NULL
                """))
            .ToList();
        vectors.Count.ShouldBeGreaterThan(1000, "the probe needs the real corpus to mean anything");

        // The committed corpus is ONE project and ONE context, so it cannot exhibit partition waste
        // at all — the waste is a property of many small partitions. The live bank's measured shape is
        // 20 distinct ctx values of which 13 hold fewer than 10 rows (2026-08-14 review, H11), so the
        // partition assignment is synthesised to that shape over the real vectors. Stated rather than
        // hidden: the vectors are real, the partition distribution is not.
        const int Partitions = 20;
        const int SmallPartitions = 13;
        var synthetic = new List<(long Id, string Ctx, string Scope, byte[] Embedding)>(vectors.Count);
        var small = vectors.Take(SmallPartitions * 5).ToList();
        var rest = vectors.Skip(small.Count).ToList();
        for (var i = 0; i < small.Count; i++)
        {
            synthetic.Add((small[i].Id, $"small:{i / 5}", small[i].Scope, small[i].Embedding));
        }

        for (var i = 0; i < rest.Count; i++)
        {
            synthetic.Add((rest[i].Id, $"big:{i % (Partitions - SmallPartitions)}", rest[i].Scope, rest[i].Embedding));
        }

        vectors = synthetic;
        var probe = vectors[0].Embedding;
        var shapes = new (string Name, string Ddl, Func<(long, string, string, byte[]), object> Key)[]
        {
            ("ctx-partitioned (today)", "ctx TEXT partition key, embedding float[384] distance_metric=cosine",
                row => row.Item2),
            ("scope-partitioned", "ctx TEXT partition key, embedding float[384] distance_metric=cosine",
                row => row.Item3),
            ("unpartitioned", "embedding float[384] distance_metric=cosine", _ => string.Empty),
            // The shape a CORRECT replacement needs: no partition key, but ctx still filterable as a
            // vec0 metadata column, so the KNN stays scoped to the caller's context. The plain
            // unpartitioned row above is NOT a correct replacement — it returns the global top-k.
            ("metadata-ctx", "ctx TEXT, embedding float[384] distance_metric=cosine", row => row.Item2)
        };

        _output.WriteLine($"corpus: {vectors.Count} vectors, "
                          + $"{vectors.Select(v => v.Ctx).Distinct().Count()} distinct ctx, "
                          + $"{vectors.Select(v => v.Scope).Distinct().Count()} distinct scope");

        foreach (var (name, ddl, key) in shapes)
        {
            var table = "probe_" + name.Split(' ')[0].Replace('-', '_');
            await connection.ExecuteAsync($"CREATE VIRTUAL TABLE {table} USING vec0({ddl});");

            var partitioned = !ddl.StartsWith("embedding", StringComparison.Ordinal);
            foreach (var row in vectors)
            {
                await connection.ExecuteAsync(
                    partitioned
                        ? $"INSERT INTO {table}(rowid, ctx, embedding) VALUES (@id, @ctx, @vec)"
                        : $"INSERT INTO {table}(rowid, embedding) VALUES (@id, @vec)",
                    new { id = row.Id, ctx = key((row.Id, row.Ctx, row.Scope, row.Embedding)), vec = row.Embedding });
            }

            // dbstat is not compiled into this SQLite build, so allocated bytes are computed the way
            // the review computed them: chunks are fixed-capacity, and every blob is
            // chunkSize x dimension x sizeof(float) whether the chunk is full or nearly empty.
            var chunks = await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}_chunks");
            var chunkSize = await connection.ExecuteScalarAsync<long>($"SELECT MAX(size) FROM {table}_chunks");
            var bytes = chunks * chunkSize * 384 * sizeof(float);

            var where = partitioned ? "ctx = @ctx AND " : string.Empty;
            var sql = $"SELECT rowid FROM {table} WHERE {where}embedding MATCH @vec AND k = {K}";
            var args = partitioned
                ? new { ctx = key((vectors[0].Id, vectors[0].Ctx, vectors[0].Scope, probe)), vec = probe }
                : (object)new { vec = probe };

            for (var i = 0; i < Warmups; i++)
            {
                await connection.QueryAsync<long>(sql, args);
            }

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < Runs; i++)
            {
                await connection.QueryAsync<long>(sql, args);
            }

            watch.Stop();
            _output.WriteLine(
                $"{name,-26} chunks={chunks,4}  chunkBytes={bytes,12:N0}  knn={watch.Elapsed.TotalMilliseconds / Runs,7:F3} ms");
        }
    }
}
