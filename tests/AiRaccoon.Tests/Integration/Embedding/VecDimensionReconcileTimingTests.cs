using System.Diagnostics;
using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Plan D3's designed measurement: the NO-CHANGE reconcile path (dimensions already match) must
///     stay invisible against a real server start. The bank side
///     (<see cref="VecDimensionReconciler.NeedsRecreateAsync" />) is one `sqlite_master` SELECT per
///     vec table — O(1) in bank size — so the size of the restored fixture barely matters; the
///     expensive, UNCACHED half is <see cref="EmbeddingManifestLoader.Load" />, confirmed here by
///     reading disk on every one of the 20 iterations rather than resolving from a cache (there is
///     none — <c>EmbeddingService.ManifestDescriptorFor</c> calls <c>manifestDescriptor.Load</c>
///     fresh every time, see <c>EmbeddingService.cs:200-209</c>).
///     <para />
///     Fixture note: the plan asked for a bank restored from a ≥200 MB copy when one exists in test
///     resources. It does not — the largest fixture in Resources/ is <c>jsaa-memory.db</c> at 18 MB —
///     so this uses that and says so here rather than inventing a synthetic multi-hundred-MB bank.
///     This does not weaken the measurement: the reconciler's bank-side cost is dimension-check-only
///     (never a repopulate) and does not scale with bank size.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class VecDimensionReconcileTimingTests(ITestOutputHelper output) : IDisposable
{
    private const int Iterations = 20;
    private const double BudgetMs = 25;

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a lingering temp dir does not fail the test.
            }
        }
    }

    [Fact]
    public async Task NoChangePath_P95StaysWithinTheTimingBudget()
    {
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-reconcile-timing");
        try
        {
            var dbPath = Path.Combine(dataRoot, "memory.db");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db"), dbPath);

            var manifestDir = WriteManifestDir(dimensions: 384);
            await ConfigureManifestEngineAsync(dataRoot, manifestDir);

            var entryEmbedder = RealEntryEmbedder();
            var factory = new SqliteConnectionFactory(TestData.CreateInfrastructureOptions(dataRoot),
                NullKeyProvider.Resolver(TestData.CreateInfrastructureOptions(dataRoot)));

            await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                (await TableSqlAsync(connection, "vec_entries")).ShouldContain("float[384]",
                    customMessage: "arrangement check: this must be the no-change path, not a real reconcile");
            }

            var samples = new List<double>(Iterations);
            await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                for (var i = 0; i < Iterations; i++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    await entryEmbedder.ReconcileVecDimensionsAsync(connection, TestContext.Current.CancellationToken);
                    stopwatch.Stop();
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            var median = Percentile(samples, 0.50);
            var p95 = Percentile(samples, 0.95);

            var bootMs = await MeasureServerStartWallClockAsync(dataRoot);
            var percentOfBoot = p95 / bootMs * 100;

            output.WriteLine($"iterations={Iterations} median={median:F3}ms p95={p95:F3}ms "
                              + $"server-start={bootMs:F0}ms p95-as-%-of-boot={percentOfBoot:F3}%");

            p95.ShouldBeLessThanOrEqualTo(BudgetMs,
                $"the no-change reconcile must be invisible next to a server start; samples: {string.Join(", ", samples.Select(s => s.ToString("F2")))}");
            percentOfBoot.ShouldBeLessThanOrEqualTo(1,
                "the plan's second threshold: p95 must also stay under 1% of a measured server-start wall clock");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     Proves the measurement can fail (`prove-the-check-fails`): the same harness, with the
    ///     reconciler replaced by a stub that sleeps 60 ms, must report a p95 that blows the budget.
    ///     Run once by hand against the real assertion (p95 &lt;= 25 ms) before this permanent,
    ///     inverted form was committed — that RED is pasted in the PR description.
    /// </summary>
    [Fact]
    public async Task NoChangePath_WithASlowStubReconciler_FailsTheBudget()
    {
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-reconcile-timing-stub");
        try
        {
            var dbPath = Path.Combine(dataRoot, "memory.db");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db"), dbPath);

            var manifestDir = WriteManifestDir(dimensions: 384);
            await ConfigureManifestEngineAsync(dataRoot, manifestDir);

            var entryEmbedder = new EntryEmbedder(RealEmbeddingService(),
                new SqliteModelMigrationLease(TimeProvider.System), new FakeTimeProvider(),
                new SlowStubReconciler(TimeSpan.FromMilliseconds(60)));
            var factory = new SqliteConnectionFactory(TestData.CreateInfrastructureOptions(dataRoot),
                NullKeyProvider.Resolver(TestData.CreateInfrastructureOptions(dataRoot)));

            var samples = new List<double>(Iterations);
            await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
                for (var i = 0; i < Iterations; i++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    await entryEmbedder.ReconcileVecDimensionsAsync(connection, TestContext.Current.CancellationToken);
                    stopwatch.Stop();
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            var p95 = Percentile(samples, 0.95);
            output.WriteLine($"stub p95={p95:F3}ms (budget {BudgetMs}ms)");

            p95.ShouldBeGreaterThan(BudgetMs,
                "a 60ms-per-call stub must blow the 25ms budget — this is what proves the real test's assertion can fail");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private static EntryEmbedder RealEntryEmbedder() =>
        new(RealEmbeddingService(), new SqliteModelMigrationLease(TimeProvider.System), new FakeTimeProvider(),
            new VecDimensionReconciler());

    private static EmbeddingService RealEmbeddingService() =>
        new(new FakeLogger<EmbeddingService>(), new LocalTokenizer(), new EmbeddingTokenizerFactory(),
            new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()));

    private static async Task ConfigureManifestEngineAsync(string dataRoot, string manifestDir)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES (@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new[]
            {
                new { key = EmbeddingSettingsKeys.Provider, value = "local" },
                new { key = EmbeddingSettingsKeys.Model, value = manifestDir }
            }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Times one real `serve` boot (start to the URL line) against the same seeded bank,
    /// as the denominator for the plan's "≤ 1% of server-start wall clock" threshold.</summary>
    private static async Task<double> MeasureServerStartWallClockAsync(string dataRoot)
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        var stopwatch = Stopwatch.StartNew();
        await using var run = ServeHarness.Start(["--data-root", dataRoot, "serve", "--port", port.ToString()]);
        await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        stopwatch.Stop();
        await run.StopAsync();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static double Percentile(IReadOnlyList<double> samples, double fraction)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        var index = Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static async Task<string> TableSqlAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: TestContext.Current.CancellationToken))
        ?? throw new InvalidOperationException($"{table} does not exist");

    /// <summary>Same recipe as EmbeddingManifestLoaderTests.WriteModelDir: a minimal, valid v1
    /// manifest directory so `Load` does real disk I/O against a real BERT-shaped model.</summary>
    private string WriteManifestDir(int dimensions)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-reconcile-timing-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, "vocab.txt"), "vocab");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");

        var manifest = new JsonObject
        {
            ["manifestVersion"] = 1,
            ["model"] = "timing-bert",
            ["source"] = new JsonObject { ["repo"] = "org/timing-bert", ["revision"] = "main", ["provider"] = "huggingface" },
            ["provider"] = "local",
            ["dimensions"] = dimensions,
            ["contextWindowTokens"] = 256,
            ["normalization"] = "l2",
            ["tokenizer"] = new JsonObject
            {
                ["family"] = "bert-wordpiece",
                ["files"] = new JsonArray(new JsonObject { ["path"] = "vocab.txt", ["sha256"] = ShaOf("vocab") })
            },
            ["onnx"] = new JsonObject
            {
                ["files"] = new JsonArray(new JsonObject { ["path"] = "model.onnx", ["sha256"] = ShaOf("model") }),
                ["inputs"] = new JsonArray("input_ids", "attention_mask", "token_type_ids"),
                ["tokenEmbeddingsOutput"] = "last_hidden_state"
            },
            ["pooling"] = new JsonObject { ["mode"] = "mean" }
        };
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest.ToJsonString());
        return dir;
    }

    private static string ShaOf(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class SlowStubReconciler(TimeSpan delay) : IVecDimensionReconciler
    {
        public async Task<bool> ReconcileAsync(SqliteConnection connection, int targetDimension,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
}
