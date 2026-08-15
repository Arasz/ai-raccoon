using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     ADR-0047 gate on the JSAA corpus: the shipped defaults hand the caller every result it asked
///     for, and the relative floor is a rank-shaped control that carries no absolute match quality.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RelativeScoreFloorTests : IDisposable
{
    private const string ProjectId = "job-search-ai-assistant";
    private const int SearchLimit = 20;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
    };

    /// <summary>Off-corpus queries: nothing in a job-search-assistant bank answers these.</summary>
    private static readonly string[] OffCorpusQueries =
    [
        "zygomatic tessellation of pelagic thermoclines",
        "how to braise a wombat in aspic"
    ];

    private readonly string _dataRoot;
    private readonly SqliteMemoryStore _store;

    public RelativeScoreFloorTests()
    {
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-relative-floor");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db"),
            Path.Combine(_dataRoot, "memory.db"));

        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The shipped default must not truncate: at MinRelativeScore 0.7 ten of the 44 baseline
    ///     queries returned fewer than the 20 they asked for (A1 17, A2 14, E3 12 — see ADR-0047).
    /// </summary>
    [Fact]
    public async Task ShippedDefaults_ReturnEveryResultTheCallerAskedFor()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureModelAsync(ct);

        var truncated = new List<string>();
        foreach (var query in LoadQueries())
        {
            var results = (await _store.SearchAsync(
                new SearchQuery(ProjectId, query.Query, SearchScope.Project, Limit: SearchLimit), ct)).Results;
            if (results.Count != SearchLimit)
            {
                truncated.Add($"{query.Id}={results.Count}");
            }
        }

        truncated.ShouldBeEmpty(
            $"the shipped search defaults silently dropped results the caller asked for: {string.Join(", ", truncated)}");
    }

    /// <summary>
    ///     The ranking is max-normalized, so rank 1 scores 1.0 whatever the corpus holds. An agent
    ///     must not read a high score as evidence of a good match — that is why the floor is named
    ///     relative and why "no useful hit" needs a different signal (ADR-0047).
    /// </summary>
    [Fact]
    public async Task OffCorpusQueries_StillScoreOneAtRankOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureModelAsync(ct);

        foreach (var text in OffCorpusQueries)
        {
            var results = (await _store.SearchAsync(
                new SearchQuery(ProjectId, text, SearchScope.Project, Limit: 5), ct)).Results;

            results.ShouldNotBeEmpty(text);
            results[0].Ranking.ShouldBe(1.0, 1e-9,
                $"'{text}' has no answer in the bank yet still tops out at 1.0 — the score is relative, not absolute");
        }
    }

    private static async Task EnsureModelAsync(CancellationToken cancellationToken)
    {
        var ensured = await TestData.CreateBundledModel().EnsureAsync(cancellationToken);
        ensured.AllPresent.ShouldBeTrue(
            $"bundled embedding model must be provisioned: {string.Join("; ", ensured.Errors)}");
    }

    private static BaselineMetricsTests.BaselineQuery[] LoadQueries()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir!.FullName, "scripts", "baseline-queries.json");
        return JsonSerializer.Deserialize<BaselineMetricsTests.BaselineQuery[]>(File.ReadAllText(path), JsonOptions)
               ?? [];
    }
}
