using System.Collections;
using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     Plan D3 (docs/work/2026-08-21-delta-review-fix-plan.md): the dimension reconcile also runs at
///     `serve` open, before the first tool call, so a serverless <c>model set</c> — one that changed
///     the engine's dimension without a server around to drain the migration — is caught the moment
///     a real server next starts. Server-only by construction (`cli-asks-the-server-acts`): the only
///     call site is <see cref="AiRaccoon.Hosting.Node.NodeRunner" />, which only runs under `serve`.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class VecDimensionReconcileAtStartTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-reconcile-at-start");
    private readonly SqliteConnectionFactory _factory;

    public VecDimensionReconcileAtStartTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task AServerlessModelSetChangingDimensions_IsReconciledBeforeTheFirstToolCall()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        // Models a serverless `model set`: the engine settings changed (as if a background,
        // short-lived server serviced the outbox write) but nothing ever drained the migration —
        // vec0 is still declared at the schema default (384).
        await SeedEngineWithoutDrainAsync("openai", dimensions: 1024);

        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        (await VecTableSqlAsync("vec_entries")).ShouldContain("float[1024]",
            customMessage: "the next serve must reconcile vec0 to the changed engine before it accepts a tool call");
        (await VecTableSqlAsync("vec_structure")).ShouldContain("float[1024]");
    }

    [Fact]
    public async Task AMatchingDimension_PerformsNoDdl()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        // The bundled local engine embeds at 384, which is exactly what MemorySchema.EnsureAsync
        // declares vec0 at — a matching engine, seeded with a real vec0 row already in place.
        await SeedMatchingEngineWithEmbeddedRowAsync();

        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        // A reconcile that (incorrectly) ran DDL on a match would DROP + recreate the table and,
        // per D3, never repopulate it — the seeded row would be gone. Its survival is the proof.
        (await VecRowCountAsync("vec_entries")).ShouldBe(1,
            customMessage: "a matching-dimension reconcile must not DROP+CREATE the table and lose its row");
        (await VecTableSqlAsync("vec_entries")).ShouldContain("float[384]");
    }

    [Fact]
    public async Task AServerlessCodeActivationChangingDimensions_IsReconciledBeforeTheFirstToolCall()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        // Models a serverless code activation at 1024: the settings rows were written but no
        // server was around to reconcile — vec_code is still at the schema default (768).
        await SeedCodeEngineWithoutReconcileAsync(dimensions: 1024, withDimensionsRow: true);

        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        (await VecTableSqlAsync("vec_code")).ShouldContain("float[1024]",
            customMessage: "the next serve must reconcile vec_code to the code engine's dimension before a tool call");
    }

    [Fact]
    public async Task ALegacyCodeBankWithoutCodeDimensionsRow_DefaultsTo768AndPerformsNoDdl()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        // A pre-1.35 bank: codeModel set, NO codeDimensions row, and a real vec_code row at 768.
        await SeedCodeEngineWithoutReconcileAsync(dimensions: 768, withDimensionsRow: false, seedEmbeddedRow: true);

        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        (await VecRowCountAsync("vec_code")).ShouldBe(1,
            customMessage: "a missing codeDimensions row must default to 768 and NOT drop the populated index");
        (await VecTableSqlAsync("vec_code")).ShouldContain("float[768]");
    }

    /// <summary>
    ///     The hard-invariant guard (`cli-asks-the-server-acts`), narrow by design: every leaf CLI
    ///     command type except <see cref="ServeCommands" /> (the one path that IS becoming the
    ///     server, not asking one to act) must never hold a live <see cref="IEntryEmbedder" /> as
    ///     its OWN constructed field. This only covers the constructor-injected-field reachability
    ///     vector — a reconcile-shaped member added to a PORT the CLI receives as a method argument
    ///     instead (e.g. <c>IMemoryStore</c>, which <c>ConfigCommands</c> holds and threads into
    ///     every leaf verb call) would slip past a field walk entirely, since the leaf command type
    ///     never holds that port as a field. That vector is covered separately, and more directly, by
    ///     <c>LayeringRulesTests.CliReachablePorts_ExposeNoReconcileOrVecDdlMember</c>, which asserts
    ///     on the port's own member surface instead of an object graph.
    ///     Mirrors <c>CliCommandsDoNotOpenTheBankTests</c>'s derive-from-the-DI-graph technique
    ///     rather than a hand-maintained list (`derive-or-delete-the-list`).
    /// </summary>
    [Fact]
    public void NoLeafCommandTypeOtherThanServe_HoldsALiveEntryEmbedderAsAConstructedField()
    {
        var leafCommandTypes = DeriveLeafCommandTypesFromTheCommandRegistration();
        leafCommandTypes.Length.ShouldBeGreaterThanOrEqualTo(8);

        using var provider = BuildCliServiceProvider();

        var violations = new List<string>();
        foreach (var commandType in leafCommandTypes)
        {
            if (commandType == typeof(ServeCommands))
            {
                continue; // sanctioned: running `serve` IS becoming the server (NodeRunner.cs:113).
            }

            var instance = provider.GetRequiredService(commandType);
            var path = FindLiveEntryEmbedder(instance);
            if (path is not null)
            {
                violations.Add($"{commandType.Name} reaches a live IEntryEmbedder via {path}");
            }
        }

        violations.ShouldBeEmpty(
            $"only {nameof(ServeCommands)} may reach a live {nameof(IEntryEmbedder)} — offenders:\n{string.Join('\n', violations)}");
    }

    /// <summary>
    ///     The positive control the rule above needs: proves <see cref="FindLiveEntryEmbedder" /> can
    ///     actually find an embedder that IS there (planted as a field, exactly like
    ///     <c>ServeCommands.nodeRunner.entryEmbedder</c>), not merely fail to find ones that aren't.
    /// </summary>
    [Fact]
    public void FindLiveEntryEmbedder_DetectsAPlantedField()
    {
        var planted = new FixtureHoldingAnEntryEmbedder(new UnusedEntryEmbedderStub());

        var path = FindLiveEntryEmbedder(planted);

        path.ShouldNotBeNull();
        path.ShouldContain(nameof(FixtureHoldingAnEntryEmbedder.Embedder));
    }

    private sealed class FixtureHoldingAnEntryEmbedder(IEntryEmbedder embedder)
    {
        public IEntryEmbedder Embedder { get; } = embedder;
    }

    private sealed class UnusedEntryEmbedderStub : IEntryEmbedder
    {
        public Task<EmbeddingConfig> ConfigureAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static Type[] DeriveLeafCommandTypesFromTheCommandRegistration()
    {
        var configCommandsConstructor = typeof(ConfigCommands)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();

        return
        [
            .. configCommandsConstructor.GetParameters()
                .Select(p => p.ParameterType)
                .Where(t => t.Name.EndsWith("Commands", StringComparison.Ordinal))
        ];
    }

    private ServiceProvider BuildCliServiceProvider()
    {
        var services = new ServiceCollection();
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        services.AddLogging();
        services.RegisterCoreMemoryServices(options);
        services.RegisterCommands();

        var lazyServerStore = new LazyServerSettingsStore(_ =>
            throw new InvalidOperationException(
                "ai-raccoon: the acquire delegate must never run while this test only constructs commands"));
        services.AddSingleton<ISettingsStore>(lazyServerStore);
        services.AddSingleton<IModelMigrationStore>(lazyServerStore);
        services.AddSingleton<IRepairStore>(lazyServerStore);
        services.AddSingleton<IPromotionQueuePruneStore>(lazyServerStore);
        services.AddSingleton<IMaintenanceStatsStore>(lazyServerStore);
        services.AddSingleton<INoiseSummaryStore>(lazyServerStore);
        services.AddSingleton<IWatchRegisteredStore>(lazyServerStore);

        return services.BuildServiceProvider();
    }

    /// <summary>Same bounded, delegate-averse walk as <c>FindLiveBankConnectionFactory</c>, aimed at
    /// <see cref="IEntryEmbedder" /> instead of a connection factory.</summary>
    private static string? FindLiveEntryEmbedder(object root)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Walk(root, root.GetType().Name, 0);

        string? Walk(object obj, string path, int depth)
        {
            if (depth > 8 || !visited.Add(obj))
            {
                return null;
            }

            if (obj is IEntryEmbedder)
            {
                return path;
            }

            var type = obj.GetType();
            if (type.Namespace is null || !type.Namespace.StartsWith("AiRaccoon", StringComparison.Ordinal))
            {
                return null;
            }

            if (typeof(Delegate).IsAssignableFrom(type))
            {
                return null;
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                if (value is null)
                {
                    continue;
                }

                if (value is IEnumerable enumerable and not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        var foundInItem = Walk(item, $"{path}.{field.Name}[]", depth + 1);
                        if (foundInItem is not null)
                        {
                            return foundInItem;
                        }
                    }

                    continue;
                }

                var found = Walk(value, $"{path}.{field.Name}", depth + 1);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Writes engine settings straight into the bank without ever opening a migration —
    /// the state a serverless `model set` leaves behind when nothing drains it.</summary>
    private async Task SeedEngineWithoutDrainAsync(string provider, int dimensions)
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES (@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new[]
            {
                new { key = EmbeddingSettingsKeys.Provider, value = provider },
                new { key = EmbeddingSettingsKeys.Dimensions, value = dimensions.ToString() }
            }, cancellationToken: Ct));
    }

    private async Task SeedMatchingEngineWithEmbeddedRowAsync()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES (@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" }, cancellationToken: Ct));
        var vector = new byte[384 * sizeof(float)];
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries(hash, value, project_id, scope, created_at, updated_at, embed_state)
            VALUES ('h1', 'a seeded value', 'p', 'project', 0, 0, 'pending')
            """, cancellationToken: Ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded', embedding = @vector WHERE hash = 'h1'",
            new { vector }, cancellationToken: Ct));
    }

    /// <summary>Writes code-engine settings + optionally a 768-dim embedded code row straight into
    /// the bank without ever going through activation — the state a serverless code activation or
    /// a pre-1.35 bank leaves behind (vec_code still at the schema default).</summary>
    private async Task SeedCodeEngineWithoutReconcileAsync(int dimensions, bool withDimensionsRow,
        bool seedEmbeddedRow = false)
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        var rows = new List<object>
        {
            new { key = EmbeddingSettingsKeys.CodeModel, value = "/models/code-engine" },
            new { key = EmbeddingSettingsKeys.CodeEngine, value = "local:/models/code-engine" }
        };
        if (withDimensionsRow)
        {
            rows.Add(new { key = EmbeddingSettingsKeys.CodeDimensions, value = dimensions.ToString() });
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES (@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            rows, cancellationToken: Ct));

        if (seedEmbeddedRow)
        {
            var vector = new byte[768 * sizeof(float)];
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                VALUES (1, 'code-hash', 'src/foo.cs', 'seed row', 'src/foo.cs', 1, 1, 'acme', 1, 1)
                """, cancellationToken: Ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE code_entries SET embed_state = 'embedded', embedding = @vector WHERE id = 1",
                new { vector }, cancellationToken: Ct));
        }
    }

    private async Task<string> VecTableSqlAsync(string table)
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: Ct))
            ?? throw new InvalidOperationException($"{table} does not exist");
    }

    private async Task<long> VecRowCountAsync(string table)
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM {table}", cancellationToken: Ct));
    }

    /// <summary>Serialized with the other env-var tests: AIRACCOON_DB_PASSPHRASE is process-global and
    /// must be cleared so a dev machine's value cannot poison a fresh-bank test.</summary>
    private static async Task<IDisposable> AcquireCleanEnvAsync(CancellationToken cancellationToken)
    {
        await TestData.EnvVarGate.WaitAsync(cancellationToken);
        var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(original);
    }

    private sealed class EnvRestore(string? original) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            TestData.EnvVarGate.Release();
        }
    }
}
