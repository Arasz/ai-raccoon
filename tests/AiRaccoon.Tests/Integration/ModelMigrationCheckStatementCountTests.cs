using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SQLitePCL;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Pins the cost ADR-0076's ToolGate migration check pays on every MCP tool call.
///     <see cref="SqliteMemoryStore.HasOpenModelMigrationAsync" /> opens a second, full bank
///     connection before every tool call — <see cref="OpensOneExtraBankConnection_OnEveryCall" />
///     documents that this is unavoidable without threading a shared connection through every one
///     of the seven guarded tool classes (out of scope for this fix; see ADR-0076
///     §"ToolGate's migration check cost").
///     <para />
///     What IS in scope, and what <see cref="MemorySchema.EnsureCheapAsync" /> exists to fix: that
///     extra connection used to pay <c>MemorySchema.EnsureAsync</c>'s whole digest-matches path (4
///     statements, <see cref="MemorySchemaDdlStatementCountTests" />) on top of its own query — for
///     a check that does not need EnsureAsync's schema-repair probes, only its digest read.
///     <see cref="EnsureCheapAsync_WhenTheDigestMatches_CostsOneStatementInsteadOfFour" /> traces the
///     real connection handle the same way <see cref="MemorySchemaDdlStatementCountTests" /> does —
///     not the factory, whose own pragmas run before the test can attach a trace.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ModelMigrationCheckStatementCountTests
{
    [Fact]
    public async Task EnsureCheapAsync_WhenTheDigestMatches_CostsOneStatementInsteadOfFour()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var statements = await TraceAsync(connection, c => MemorySchema.EnsureCheapAsync(c, TestContext.Current.CancellationToken));

        // Just the application_id read — none of EnsureAsync's user_version read or its two
        // indexed repair probes, which a migration check does not need.
        statements.ShouldBe(["PRAGMA application_id"]);
    }

    [Fact]
    public async Task EnsureCheapAsync_WhenTheDigestIsStale_FallsBackToTheFullEnsure()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA application_id = 0", cancellationToken: TestContext.Current.CancellationToken));

        var statements = await TraceAsync(connection, c => MemorySchema.EnsureCheapAsync(c, TestContext.Current.CancellationToken));

        // Falls through to the real EnsureAsync, which re-reads application_id itself (one read's
        // worth of overlap this rare, self-healing path accepts) then runs the Ddl and re-stamps
        // the digest — this check's own read, EnsureAsync's read, and EnsureAsync's stamp: 3.
        statements.Count(s => s.Contains("CREATE ", StringComparison.Ordinal)).ShouldBeGreaterThan(0);
        statements.Count(s => s.Contains("PRAGMA application_id", StringComparison.Ordinal)).ShouldBe(3);
    }

    /// <summary>
    ///     Documents the residual cost this fix does not remove: ToolGate's migration check still
    ///     opens a connection of its own, separate from whatever the guarded operation opens next —
    ///     sharing one connection across both would mean threading it through every one of the seven
    ///     tool classes and every store method they call, which this task's scope does not cover.
    /// </summary>
    [Fact]
    public async Task OpensOneExtraBankConnection_OnEveryCall()
    {
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-migration-check-opens");
        try
        {
            var options = TestData.CreateInfrastructureOptions(dataRoot);
            var real = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            var factory = new OpenCountingConnectionFactory(real);
            var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
                new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), TimeProvider.System,
                TestData.CreateEmbeddingService());

            // Warm-up: ensure the schema before measuring, matching MemorySchemaDdlStatementCountTests'
            // and the WP5 docs' own convention — the digest-stale first-open cost is a one-time,
            // separately-pinned concern, not this test's subject.
            await using (await real.OpenBankAsync(TestContext.Current.CancellationToken))
            {
            }

            factory.Opens = 0;
            await store.HasOpenModelMigrationAsync(TestContext.Current.CancellationToken);

            factory.Opens.ShouldBe(1);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private static async Task<List<string>> TraceAsync(SqliteConnection connection, Func<SqliteConnection, Task> action)
    {
        var statements = new List<string>();
        strdelegate_trace tracer = (_, sql) => statements.Add(sql);
        raw.sqlite3_trace(connection.Handle, tracer, null);
        await action(connection);
        raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);
        return statements;
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    /// <summary>Counts <see cref="ISqliteConnectionFactory.OpenBankAsync" />/<see cref="ISqliteConnectionFactory.OpenBankSkippingEnsureAsync" />
    /// calls only — no statement tracing, since a connection's own pragmas and EnsureAsync already
    /// ran by the time this wrapper receives it back (the blind spot <see cref="MemorySchemaDdlStatementCountTests" />-style
    /// tracing avoids by tracing an already-open connection directly instead).</summary>
    private sealed class OpenCountingConnectionFactory(ISqliteConnectionFactory inner) : ISqliteConnectionFactory
    {
        public int Opens { get; set; }

        public string BankPath => inner.BankPath;

        public Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
        {
            Opens++;
            return inner.OpenBankAsync(cancellationToken);
        }

        public Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default)
        {
            Opens++;
            return inner.OpenBankSkippingEnsureAsync(cancellationToken);
        }

        public Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default) => inner.MigrateLegacyKeyAsync(cancellationToken);

        public Task<SqliteConnection> OpenBankWithResolvedKeyAsync(
            ResolvedKey resolvedKey, CancellationToken cancellationToken = default) =>
            inner.OpenBankWithResolvedKeyAsync(resolvedKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default) => inner.RekeyBankAsync(newKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default) => inner.RekeyBankAsync(newKey, currentKey, cancellationToken);

        public Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default) => inner.OpenBankWithKeyAsync(key, cancellationToken);
    }
}
