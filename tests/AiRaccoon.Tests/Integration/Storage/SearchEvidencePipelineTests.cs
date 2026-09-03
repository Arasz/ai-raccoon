using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using AiRaccoon.Tests.Unit.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     P4 pipeline wiring through the real <see cref="SqliteMemoryStore" />: S1 evidence
///     captured at the fuse seam reaches the served envelope keyed by hash, and capturing
///     it issues no new SQL (G5). The search path performs only in-memory dictionary
///     carries over already-materialized lists — the Trace pin below fails the moment a
///     query is added to it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchEvidencePipelineTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-search-evidence");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     Counts every SQL statement executed on connections this factory hands out —
    ///     an <c>sqlite3_trace</c> hook fires per executed statement, so the search path's
    ///     query count is observed, not assumed (<c>SqliteConnection.Trace</c> does not exist
    ///     on Microsoft.Data.Sqlite.Core 10, so the hook goes through SQLitePCLRaw directly).
    ///     One hook per connection: the factory may pool, and double-hooking would double-count.
    ///     The hook is O(1) per statement and never issues SQL itself.
    /// </summary>
    private sealed class CountingFactory(ISqliteConnectionFactory inner, Action<string> onStatement) : ISqliteConnectionFactory
    {
        private readonly object _gate = new();

        private readonly Dictionary<SqliteConnection, SQLitePCL.strdelegate_trace> _hooks = new(ReferenceEqualityComparer.Instance);

        public string BankPath => inner.BankPath;

        public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenBankAsync(cancellationToken);
            Trace(connection);
            return connection;
        }

        public Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default) =>
            inner.MigrateLegacyKeyAsync(cancellationToken);

        public Task<SqliteConnection> OpenBankWithResolvedKeyAsync(ResolvedKey resolvedKey,
            CancellationToken cancellationToken = default) =>
            inner.OpenBankWithResolvedKeyAsync(resolvedKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, currentKey, cancellationToken);

        public async Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenBankWithKeyAsync(key, cancellationToken);
            Trace(connection);
            return connection;
        }

        public Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default) =>
            inner.OpenBankSkippingEnsureAsync(cancellationToken);

        private void Trace(SqliteConnection connection)
        {
            lock (_gate)
            {
                if (_hooks.ContainsKey(connection))
                {
                    return;
                }

                // Rooted in _hooks for the connection's lifetime: an unrooted delegate would
                // be collected while native code still calls it.
                SQLitePCL.strdelegate_trace hook = (_, sql) => onStatement(sql);
                _hooks[connection] = hook;
                SQLitePCL.raw.sqlite3_trace(connection.Handle, hook, null);
            }
        }
    }

    private SqliteMemoryStore CreateStore(ISqliteConnectionFactory factory) =>
        SearchTimingsHarness.CreateStore(factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero)),
            new SearchTimingsHarness.VectorEmbedderStub());

    private static async Task SeedAsync(SqliteMemoryStore store, CancellationToken cancellationToken)
    {
        await store.WriteAsync(new MemoryWriteRequest("proj-1", "widgets are stocked on the shelf"), cancellationToken);
        await store.WriteAsync(new MemoryWriteRequest("proj-1", "the warehouse tracks widgets every day"), cancellationToken);
        await store.WriteAsync(new MemoryWriteRequest("proj-1", "spare widgets line the back room wall"), cancellationToken);
    }

    /// <summary>
    ///     End-to-end wiring: every served row resolves its own evidence by hash, and the
    ///     response shape describes the single firing leg.
    /// </summary>
    [RetryFact]
    public async Task SearchAsync_AttachesEvidenceKeyedByHash_ToEveryServedRow()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = CreateStore(factory);
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(store, ct);

        var result = await store.SearchAsync(
            new SearchQuery("proj-1", "widgets", Scope: SearchScope.Project, VectorWeight: 0), ct);

        result.Results.Count.ShouldBe(3);
        result.EvidenceByHash.ShouldNotBeNull();
        foreach (var row in result.Results)
        {
            var evidence = result.EvidenceByHash[row.Hash];
            evidence.Hash.ShouldBe(row.Hash);
            evidence.Legs.Count.ShouldBe(1, "only the FTS leg fired, so every hash has exactly one leg vote");
            evidence.Legs[0].LegName.ShouldBe("fts");
        }

        result.EvidenceByHash[result.Results[0].Hash].FusionStrength.ShouldBe(1.0, 1e-12,
            "single-leg fusion: the top raw equals maxPossible, so the winner always scores 1.0");
        result.Stats.ShouldNotBeNull();
        result.Stats.ParticipatingLegs.ShouldBe(["fts"]);
        result.Stats.TopMargin.ShouldNotBeNull();
        result.Stats.TopVsMedian.ShouldNotBeNull();
    }

    /// <summary>
    ///     G5: capturing and carrying evidence adds zero SQL queries to the search path.
    ///     FTS-only path (vectorWeight: 0): the both-legs path has its own pin —
    ///     <c>SearchSignalPreservationStageOneTests.LiveSearch_WithVectorLegFiring_IssuesPinnedStatementCount</c>.
    ///     The pinned count is the whole FTS-only search on one project-scoped single-token query:
    ///     5 open PRAGMAs, 3 schema/watch checks, the 2-statement settings snapshot, 1 context
    ///     resolve, 1 FTS candidate query, 1 grouped snippet lookup, and 1 access bump per
    ///     served row (3). Any new query on this path — including one smuggled in by the
    ///     evidence carry — breaks the pin.
    /// </summary>
    [RetryFact]
    public async Task SearchAsync_EvidenceCapture_IssuesZeroNewQueries()
    {
        var statements = new List<string>();
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var inner = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = CreateStore(new CountingFactory(inner, sql =>
        {
            // Application-issued statements only: SQLite prefixes FTS5-internal shadow-table
            // traffic (entries_fts_idx/docsize lookups, data_version polls) with "--", and that
            // traffic scales with index segment structure rather than with this repo's code.
            // The gate below guards the queries P4 could have added — Dapper-level statements.
            if (!sql.StartsWith("--", StringComparison.Ordinal))
            {
                statements.Add(sql);
            }
        }));
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(store, ct);

        statements.Clear();
        var result = await store.SearchAsync(
            new SearchQuery("proj-1", "widgets", Scope: SearchScope.Project, VectorWeight: 0), ct);

        foreach (var statement in statements)
        {
            output.WriteLine(statement);
        }

        result.EvidenceByHash.ShouldNotBeNull("the count pin is meaningless unless evidence actually flowed");
        result.Results.Count.ShouldBe(3);
        statements.Count.ShouldBe(ExpectedStatementCount);
    }

    // Pinned by running SearchAsync_EvidenceCapture_IssuesZeroNewQueries: the count the
    // FTS-only search path issues today, with evidence flowing. Deliberate, not incidental —
    // adding a query to the search path means updating this literal (SearchResultsTests.PhaseNames
    // precedent: the pin is the review gate). Pair-update with the P7 G5 conjunction pin
    // (SearchSignalPreservationStageOneTests.EquippedSearch_AddsExactlyOneStatementBeyondTheUnequippedPath,
    // same 16 re-proven through the full tools path) and the P7 vector-path pin
    // (LiveSearch_WithVectorLegFiring_IssuesPinnedStatementCount): any search-path query change
    // must reconcile all three.
    private const int ExpectedStatementCount = 16;
}
