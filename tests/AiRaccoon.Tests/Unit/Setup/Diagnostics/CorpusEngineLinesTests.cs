using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Diagnostics;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Diagnostics;

/// <summary>
///     The extraction's Fast proof: the per-corpus descriptors pair each corpus with its own
///     wiring (a swapped key, table, query or label goes RED here, structurally, in the lane a
///     developer runs before pushing), and the line grammar is pinned to the frozen literals of
///     the doctor contract (P1 §2.3; R1 M2). Pure component — no bank needed except the one
///     alias-pin row that executes <see cref="MemorySql.SelectModelMigration" /> for the first
///     time anywhere (R1 S9).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CorpusEngineLinesTests
{
    [Fact]
    public void EngineDescriptors_EachCorpusPairsItsOwnKeyTableAndQuery()
    {
        var code = CorpusEngineProbe.Code;
        code.Label.ShouldBe("code");
        code.ProviderKey.ShouldBeNull(); // the code engine is always local by construction
        code.ModelKey.ShouldBe(EmbeddingSettingsKeys.CodeModel);
        code.BaseUrlKey.ShouldBeNull();
        code.ApiKeyKey.ShouldBeNull();
        code.PendingTable.ShouldBe("code_entries");
        code.PendingSql.ShouldContain("FROM code_entries");
        code.NotConfiguredRemedy.ShouldContain("code");

        var memory = CorpusEngineProbe.Memory;
        memory.Label.ShouldBe("memory");
        memory.ProviderKey.ShouldBe(EmbeddingSettingsKeys.Provider);
        memory.ModelKey.ShouldBe(EmbeddingSettingsKeys.Model);
        memory.BaseUrlKey.ShouldBe(EmbeddingSettingsKeys.BaseUrl);
        memory.ApiKeyKey.ShouldBe(EmbeddingSettingsKeys.ApiKey);
        memory.PendingTable.ShouldBe("entries");
        memory.PendingSql.ShouldContain("FROM entries");
        memory.NotConfiguredRemedy.ShouldContain("embedding set local");
        memory.NotConfiguredRemedy.ShouldNotContain("code");

        CorpusEngineProbe.All.ShouldBe([CorpusEngineProbe.Memory, CorpusEngineProbe.Code]);
    }

    [Fact]
    public void EngineLine_CodeNotConfigured_IsByteIdenticalToToday() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Code, new CorpusEngineState(null, null, null, null))
            .ShouldBe("code engine: not configured — run 'ai-raccoon model code set default' to enable semantic code search");

    [Fact]
    public void EngineLine_CodeConfigured_IsByteIdenticalToToday() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Code,
                new CorpusEngineState("faxenoff__code-daemon-embed-v1 (manifest unreadable)", "/x/faxenoff__code-daemon-embed-v1", null, null))
            .ShouldBe("code engine: faxenoff__code-daemon-embed-v1 (manifest unreadable) (/x/faxenoff__code-daemon-embed-v1)");

    [Fact]
    public void EngineLine_UnreadableState_NamesTheDegradedArm() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Code,
                new CorpusEngineState(null, null, null, null, Unreadable: true))
            .ShouldBe("code engine: unreadable (settings table missing or unreadable)");

    [Fact]
    public void EngineLine_MemoryBundled_IsBare() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Memory, new CorpusEngineState("bundled", null, null, null))
            .ShouldBe("memory engine: bundled");

    [Fact]
    public void EngineLine_MemoryLocalDirectory_NamesTheManifestAndTheDirectory() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Memory,
                new CorpusEngineState("Salesforce/SFR-Embedding-Code-400M_R", "/models/Salesforce__SFR-Embedding-Code-400M_R", null, null))
            .ShouldBe("memory engine: Salesforce/SFR-Embedding-Code-400M_R (/models/Salesforce__SFR-Embedding-Code-400M_R)");

    [Fact]
    public void EngineLine_MemoryRemote_NamesProviderModelAndBaseUrl() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Memory,
                new CorpusEngineState("openai:text-embedding-3-small", "https://api.example.com/v1", null, null))
            .ShouldBe("memory engine: openai:text-embedding-3-small (https://api.example.com/v1)");

    [Fact]
    public void EngineLine_MemoryRemoteWithoutBaseUrl_IsBare() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Memory,
                new CorpusEngineState("openai:text-embedding-3-small", null, null, null))
            .ShouldBe("memory engine: openai:text-embedding-3-small");

    [Fact]
    public void EngineLine_MemoryRemoteWithoutApiKey_AppendsTheRemedy() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Memory,
                new CorpusEngineState("openai:text-embedding-3-small", null, EmbeddingEngineSetup.NoApiKeyRemedy, null))
            .ShouldBe("memory engine: openai:text-embedding-3-small — no API key set; run 'ai-raccoon model embedding set openai <model> --api-key <key>' or embeddings will fail");

    [Fact]
    public void EngineLine_MemoryNotConfigured_NamesTheInstallCommand() =>
        CorpusEngineLines.EngineLine(CorpusEngineProbe.Memory, new CorpusEngineState(null, null, null, null))
            .ShouldBe($"memory engine: not configured — run '{EmbeddingEngineSetup.DefaultModelCommand}' to enable semantic memory search");

    [Fact]
    public void PendingLine_PrintsTheCountInvariantCulture() =>
        CorpusEngineLines.PendingLine(CorpusEngineProbe.Memory, new CorpusEngineState("bundled", null, null, 47723))
            .ShouldBe("memory rows pending: 47723");

    [Fact]
    public void PendingLine_UnreadableCount_PrintsUnreadable() =>
        CorpusEngineLines.PendingLine(CorpusEngineProbe.Code, new CorpusEngineState(null, null, null, null))
            .ShouldBe("code rows pending: unreadable");

    /// <summary>#422's twin: the memory not-configured remedy must not become a fifth hand-spelling of the command (R1 Table 2 step 2, R2 row EmbeddingEngineSetupCommand_IsTheOnlySpelling).</summary>
    [Fact]
    public void EmbeddingEngineSetupCommand_IsTheOnlySpelling()
    {
        EmbeddingEngineSetup.DefaultModelCommand.ShouldBe("ai-raccoon model embedding set local");
        EmbeddingEngineSetup.NoApiKeyRemedy.ShouldBe(
            "no API key set; run 'ai-raccoon model embedding set openai <model> --api-key <key>' or embeddings will fail");

        var parse = CliCommandTree.BuildFullRootCommand().Parse(EmbeddingEngineSetup.DefaultModelCommand.Split(' ')[1..]);
        parse.Errors.ShouldBeEmpty(
            $"'{EmbeddingEngineSetup.DefaultModelCommand}' is quoted to users everywhere; it has to parse");
        parse.CommandResult.Command.Name.ShouldBe("local");
    }

    [Fact]
    public void DoctorProbe_QuotesTheCommandConstant() =>
        CorpusEngineProbe.Memory.NotConfiguredRemedy.ShouldContain(EmbeddingEngineSetup.DefaultModelCommand);

    [Fact]
    public void EmbeddingService_QuotesTheConstant_NotALiteral()
    {
        var source = File.ReadAllText(TestData.RepoFile("src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs"));

        source.ShouldContain("EmbeddingEngineSetup.DefaultModelCommand");
        source.ShouldNotContain("\"ai-raccoon model embedding set local\"");
    }

    /// <summary>
    ///     R1 S9: <see cref="MemorySql.SelectModelMigration" /> has never executed anywhere — doctor
    ///     is its first consumer — so its aliases are pinned here in the Fast lane against a seeded
    ///     row, not only through the Slow doctor suite.
    /// </summary>
    [Fact]
    public async Task SelectModelMigration_ProjectsItsUnixSecondsColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE model_migration (id INTEGER PRIMARY KEY CHECK (id = 1), provider TEXT NOT NULL,
                model TEXT NULL, base_url TEXT NULL, engine TEXT NOT NULL, started_at INTEGER NOT NULL,
                finished_at INTEGER NULL, lease_owner TEXT NULL, lease_expires_at INTEGER NULL);
            INSERT INTO model_migration (id, provider, model, base_url, engine, started_at, finished_at)
            VALUES (1, 'local', NULL, NULL, 'test-engine', 1787739481, NULL);
            """, cancellationToken: TestContext.Current.CancellationToken));

        var row = await connection.QuerySingleOrDefaultAsync<TestMigrationRow>(new CommandDefinition(
            MemorySql.SelectModelMigration, cancellationToken: TestContext.Current.CancellationToken));

        row.ShouldNotBeNull();
        row.StartedAt.ShouldBe(1787739481);
        row.FinishedAt.ShouldBeNull();

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE model_migration SET finished_at = 1787739482", cancellationToken: TestContext.Current.CancellationToken));
        var closed = await connection.QuerySingleOrDefaultAsync<TestMigrationRow>(new CommandDefinition(
            MemorySql.SelectModelMigration, cancellationToken: TestContext.Current.CancellationToken));

        closed.ShouldNotBeNull();
        closed.StartedAt.ShouldBe(1787739481);
        closed.FinishedAt.ShouldBe(1787739482);
    }

    /// <summary>model_migration's INTEGER unix-seconds columns as Dapper maps them — the alias contract of MemorySql.SelectModelMigration.</summary>
    private sealed record TestMigrationRow
    {
        public long StartedAt { get; init; }

        public long? FinishedAt { get; init; }
    }
}
