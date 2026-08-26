using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Setup.Diagnostics;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     GH #357: `ai-raccoon doctor` verifies the bank's schema shape and reports — it never
///     repairs. Driven end-to-end through <see cref="CliRun" /> with real argv, against a real
///     temp-dir bank, matching <c>NoiseEntriesCommandsTests</c>' style.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DoctorCommandsTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("doctor-cli");
    private readonly SqliteConnectionFactory _factory;
    private readonly InfrastructureOptions _options;

    public DoctorCommandsTests()
    {
        _options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(_options, NullKeyProvider.Resolver(_options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private Task<(int Exit, string Out, string Err)> Run(DoctorCommands doctor, string[] args) => CliRun.RunAsync(args, TestData.CreateConfigCommands(new FakeConfigStore(), doctor: doctor));

    private DoctorCommands CreateDoctor(IEncryptionKeyResolver? resolver = null) => new(_factory, resolver ?? NullKeyProvider.Resolver(_options), NullLogger<DoctorCommands>.Instance);

    /// <summary>
    ///     Delta review C3: a missing bank at the resolved path must not read as HEALTHY (0) — a
    ///     wrong `--data-root` would otherwise be indistinguishable from a healthy bank. Driven
    ///     through argv (<see cref="Run" /> → <see cref="ConfigCommands" />), not the handler
    ///     method directly.
    /// </summary>
    [RetryFact]
    public async Task NoBankAtTheResolvedPath_ExitsNonZeroAndNamesThePath()
    {
        var (exit, _, err) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.NoBank);
        exit.ShouldNotBe(ExitCode.Success);
        err.ShouldContain(_factory.BankPath);
    }

    [RetryFact]
    public async Task Doctor_HealthyBank_ReportsHealthyAndExitsZero()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(0);
        outp.ShouldContain("status: HEALTHY");
    }

    /// <summary>
    ///     The extraction's characterisation gate: the whole report, in order, line for line.
    ///     Nothing else in this suite asserts the header lines, the line count or the order — so
    ///     an extraction that dropped `user_version` would otherwise stay green. Literals on
    ///     purpose: this test IS the output contract, and it changes in the same commit the
    ///     wording does.
    /// </summary>
    [RetryFact]
    public async Task Doctor_HealthyBank_PrintsExactlyTheseLinesInThisOrder()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (exit, outp, err) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.Success);
        err.ShouldBeEmpty();
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        Lines(outp).ShouldBe([
            $"ai-raccoon doctor: {_factory.BankPath}",
            $"user_version: {MemorySchema.CurrentVersion} (this binary: {MemorySchema.CurrentVersion})",
            $"application_id: {MemorySchema.SchemaDigest} (expected: {MemorySchema.SchemaDigest})",
            $"memory engine: not configured — run '{EmbeddingEngineSetup.DefaultModelCommand}' to enable semantic memory search",
            $"code engine: not configured — run '{CodeEngineSetup.DefaultModelCommand}' to enable semantic code search",
            $"embedding threads: {threads} (halved-core default)",
            "memory rows pending: 0",
            "code rows pending: 0",
            "model migration: none open",
            "doctor verifies schema shape only; it never repairs a bank",
            "status: HEALTHY"
        ]);
    }

    /// <summary>
    ///     The threads line is shared by both corpora and must survive the extraction as exactly
    ///     one line — every existing threads assertion is a ShouldContain, which passes on 1 or 2
    ///     occurrences, so only a count can see the per-corpus-loop slip.
    /// </summary>
    [RetryFact]
    public async Task Doctor_SharedThreadsLine_AppearsExactlyOnce()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Regex.Matches(outp, "embedding threads: ").Count.ShouldBe(1);
    }

    /// <summary>
    ///     #422 review: the code-engine line's value is the manifest's model NAME, not the raw
    ///     directory — the existing assertions check the label and the directory separately, so
    ///     an extraction that dropped `ModelNameFor` entirely would keep them green.
    /// </summary>
    [RetryFact]
    public async Task Doctor_ConfiguredCodeEngine_NamesTheModelNameNotJustTheDirectory()
    {
        var modelDir = Path.Combine(_dataRoot, "models", "faxenoff__code-daemon-embed-v1");
        Directory.CreateDirectory(modelDir);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeModel, value = modelDir },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        outp.ShouldContain($"code engine: {Path.GetFileName(modelDir)} (manifest unreadable) ({modelDir})");
    }

    /// <summary>
    ///     #422: a fresh install's code corpus is stored but unsearchable-by-meaning, and nothing
    ///     said so anywhere. doctor is where someone looks when search feels wrong, so it reports
    ///     the code engine's state and — when there isn't one — the exact command that installs it.
    /// </summary>
    [RetryFact]
    public async Task Doctor_NoCodeEngine_SaysNotConfigured_AndNamesTheInstallCommand()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        // Whole line, label and remedy together: a memory line quoting the same command would
        // otherwise satisfy two separate substrings while the code line lost its remedy (P3 §4.2).
        outp.ShouldContain($"code engine: not configured — run '{CodeEngineSetup.DefaultModelCommand}' to enable semantic code search");
    }

    /// <summary>
    ///     R2 B1: the memory corpus's configured-ness is embedding.provider, NOT embedding.model —
    ///     a model row without a provider row is the FTS5-only state (P1 §2.3 arm 4), and doctor
    ///     must say so. The row that kills the ProviderKey↔ModelKey transposition in the memory
    ///     descriptor: nothing else goes RED under that swap.
    /// </summary>
    [RetryFact]
    public async Task Doctor_MemoryModelSetButNoProvider_ReportsNotConfigured()
    {
        var memoryDir = Path.Combine(_dataRoot, "models", "memory-engine-dir");
        Directory.CreateDirectory(memoryDir);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.Model, value = memoryDir },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain(
            $"memory engine: not configured — run '{EmbeddingEngineSetup.DefaultModelCommand}' to enable semantic memory search");
        outp.ShouldNotContain($"memory engine: {Path.GetFileName(memoryDir)}");
        outp.ShouldNotContain("memory engine: bundled");
    }

    /// <summary>P1 §2.3 arm 2: provider set, no model — the bundled fallback, bare. The complement of the row above; the pair pins the ProviderKey/ModelKey wiring in both directions.</summary>
    [RetryFact]
    public async Task Doctor_MemoryProviderSetWithoutAModel_ReportsTheBundledArm()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.Provider, value = "local" },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain("memory engine: bundled");
        outp.ShouldNotContain("memory engine: not configured");
    }

    [RetryFact]
    public async Task Doctor_ExplicitLocalMemoryModel_NamesTheResolvedModelAndItsPath()
    {
        var modelDir = Path.Combine(_dataRoot, "models", "memory-engine-dir");
        Directory.CreateDirectory(modelDir);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            foreach (var (key, value) in new[]
                     {
                         (EmbeddingSettingsKeys.Provider, "local"),
                         (EmbeddingSettingsKeys.Model, modelDir)
                     })
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                    new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
            }
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain(
            $"memory engine: {Path.GetFileName(modelDir)} (manifest unreadable) ({modelDir})");
    }

    /// <summary>
    ///     P1 §2.3 arm 3: a remote engine renders openai:&lt;model&gt; with the endpoint in the
    ///     parenthetical — and must NOT flow through the directory formatter, which would append
    ///     "(manifest unreadable)" to a model id (R2 M5's only unambiguous witness).
    /// </summary>
    [RetryFact]
    public async Task Doctor_RemoteMemoryProvider_NamesProviderModelAndBaseUrl_AndNeverThePath()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            foreach (var (key, value) in new[]
                     {
                         (EmbeddingSettingsKeys.Provider, "openai"),
                         (EmbeddingSettingsKeys.Model, "text-embedding-3-small"),
                         (EmbeddingSettingsKeys.BaseUrl, "https://example.invalid/v1"),
                         (EmbeddingSettingsKeys.ApiKey, "sk-secret")
                     })
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                    new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
            }
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain("memory engine: openai:text-embedding-3-small (https://example.invalid/v1)");
        outp.ShouldNotContain("text-embedding-3-small (manifest unreadable)");
        outp.ShouldNotContain("no API key set");
    }

    /// <summary>P1 §2.3 arm 3: no API key appends the em-dash clause verbatim from SettingsCommands' warning.</summary>
    [RetryFact]
    public async Task Doctor_RemoteMemoryProvider_WithoutApiKey_AppendsTheRemedy()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            foreach (var (key, value) in new[]
                     {
                         (EmbeddingSettingsKeys.Provider, "openai"),
                         (EmbeddingSettingsKeys.Model, "text-embedding-3-small")
                     })
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                    new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
            }
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain($"memory engine: openai:text-embedding-3-small — {EmbeddingEngineSetup.NoApiKeyRemedy}");
    }

    /// <summary>R1 S5: the API key's presence is all doctor ever needs — the persisted secret must never reach stdout or stderr.</summary>
    [RetryFact]
    public async Task Doctor_RemoteMemoryProvider_NeverPrintsTheApiKey()
    {
        const string apiKey = "sk-secret-1234567890";
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            foreach (var (key, value) in new[]
                     {
                         (EmbeddingSettingsKeys.Provider, "openai"),
                         (EmbeddingSettingsKeys.Model, "text-embedding-3-small"),
                         (EmbeddingSettingsKeys.ApiKey, apiKey)
                     })
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                    new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
            }
        }

        var (exit, outp, err) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(0);
        (outp + err).ShouldNotContain(apiKey);
    }

    /// <summary>
    ///     R2 B2: the number is alive — 0 on a fresh bank, then the pending predicate after real
    ///     inserts that also seed embedded rows, so a total-count bug (COUNT(*) without the WHERE)
    ///     reads 5 here, not 3.
    /// </summary>
    [RetryFact]
    public async Task Doctor_ReportsHowManyMemoryRowsArePending()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);
        Lines(outp).ShouldContain("memory rows pending: 0");

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, value, project_id, scope, created_at, updated_at, embed_state)
                VALUES ('m1', 'one', 'acme', 'project', 0, 0, 'pending'),
                       ('m2', 'two', 'acme', 'project', 0, 0, 'pending'),
                       ('m3', 'three', 'acme', 'project', 0, 0, 'pending'),
                       ('m4', 'four', 'acme', 'project', 0, 0, 'embedded'),
                       ('m5', 'five', 'acme', 'project', 0, 0, 'embedded')
                """, cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp2, _) = await Run(CreateDoctor(), ["doctor"]);
        Lines(outp2).ShouldContain("memory rows pending: 3");
    }

    /// <summary>
    ///     R2 M1/M2: the keys/query-swap killer. Two DIFFERENT model directories and TWO DIFFERENT
    ///     pending counts (2 code, 3 memory): swapping either the settings keys or the two COUNT
    ///     queries in the shared component's per-corpus descriptors reddens this test in both
    ///     directions. Whole lines only — an unanchored directory or count passes under a swap.
    /// </summary>
    [RetryFact]
    public async Task Doctor_BothCorporaConfigured_ReportsEachCorpusFromItsOwnWiring()
    {
        var codeDir = Path.Combine(_dataRoot, "models", "code-engine-dir");
        var memoryDir = Path.Combine(_dataRoot, "models", "memory-engine-dir");
        Directory.CreateDirectory(codeDir);
        Directory.CreateDirectory(memoryDir);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            foreach (var (key, value) in new[]
                     {
                         (EmbeddingSettingsKeys.CodeModel, codeDir),
                         (EmbeddingSettingsKeys.Provider, "local"),
                         (EmbeddingSettingsKeys.Model, memoryDir)
                     })
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                    new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                VALUES ('c1', 'src/A.cs', 'a', 'src/A.cs', 1, 2, 'acme', 1, 1),
                       ('c2', 'src/B.cs', 'b', 'src/B.cs', 1, 2, 'acme', 1, 1)
                """, cancellationToken: TestContext.Current.CancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, value, project_id, scope, created_at, updated_at, embed_state)
                VALUES ('m1', 'one', 'acme', 'project', 0, 0, 'pending'),
                       ('m2', 'two', 'acme', 'project', 0, 0, 'pending'),
                       ('m3', 'three', 'acme', 'project', 0, 0, 'pending')
                """, cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain($"code engine: {Path.GetFileName(codeDir)} (manifest unreadable) ({codeDir})");
        Lines(outp).ShouldContain($"memory engine: {Path.GetFileName(memoryDir)} (manifest unreadable) ({memoryDir})");
        Lines(outp).ShouldContain("code rows pending: 2");
        Lines(outp).ShouldContain("memory rows pending: 3");
    }

    /// <summary>
    ///     R2 J10/S4: the extraction's sharpest failure mode — one shared try/catch around BOTH
    ///     corpora, or a swapped PendingTable guard, blanks the code line too. This bank has a
    ///     healthy settings (so the code side is genuinely readable) and a #357-shaped entries (so
    ///     the memory count genuinely throws) — the only combination that can tell the two apart.
    /// </summary>
    [RetryFact]
    public async Task Doctor_MemoryStateUnreadable_StillReportsTheCodeEngine()
    {
        var modelDir = Path.Combine(_dataRoot, "models", "faxenoff__code-daemon-embed-v1");
        Directory.CreateDirectory(modelDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
            await raw.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE entries (only_one_column TEXT);
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """, cancellationToken: TestContext.Current.CancellationToken));
            await raw.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeModel, value = modelDir },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var beforeHash = FileSha256(_factory.BankPath);

        var (exit, outp, err) = await Run(CreateDoctor(), ["doctor"]);

        FileSha256(_factory.BankPath).ShouldBe(beforeHash, "doctor must never write to a bank it is diagnosing");
        exit.ShouldBe(ExitCode.SchemaVerificationFailed);          // the SHAPE decides, never these reads
        Lines(outp).ShouldContain($"code engine: {Path.GetFileName(modelDir)} (manifest unreadable) ({modelDir})");
        Lines(outp).ShouldContain("code rows pending: 0");         // code_entries is absent, not broken
        Lines(outp).ShouldContain("memory rows pending: unreadable");
        Lines(outp).ShouldNotContain("memory rows pending: 0");
    }

    [RetryFact]
    public async Task Doctor_MissingEntriesTable_ReportsPendingAsZero()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DROP TABLE entries", cancellationToken: TestContext.Current.CancellationToken));
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.SchemaVerificationFailed);          // a missing table IS a shape mismatch
        Lines(outp).ShouldContain("memory rows pending: 0");
        Lines(outp).ShouldContain("code rows pending: 0");
        outp.ShouldContain("status: SHAPE MISMATCH");
    }

    /// <summary>R1 S6: on the exact #357 repro bank, both engine lines name the degraded arm — never the false not-configured remedy — and the shape verdict still owns the exit code.</summary>
    [RetryFact]
    public async Task Doctor_ShapeBrokenBank_MemoryPendingIsUnreadable_AndExitIsShapeVerificationFailed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = raw.CreateCommand();
            create.CommandText = "CREATE TABLE entries (only_one_column TEXT)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.SchemaVerificationFailed);
        Lines(outp).ShouldContain("memory engine: unreadable (settings table missing or unreadable)");
        Lines(outp).ShouldContain("code engine: unreadable (settings table missing or unreadable)");
        Lines(outp).ShouldContain("memory rows pending: unreadable");
        Lines(outp).ShouldContain("code rows pending: 0");
        Lines(outp).ShouldContain("model migration: unreadable");
        Lines(outp).ShouldNotContain("code engine: not configured");
    }

    /// <summary>R2 J3/N4: dropping settings degrades both engine lines but must not blank the pending counts — they read off their own tables.</summary>
    [RetryFact]
    public async Task Doctor_MissingSettingsTable_StillReportsTheMemoryEngineAndPendingCount()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DROP TABLE settings", cancellationToken: TestContext.Current.CancellationToken));
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.SchemaVerificationFailed);
        Lines(outp).ShouldContain("memory engine: unreadable (settings table missing or unreadable)");
        Lines(outp).ShouldContain("code engine: unreadable (settings table missing or unreadable)");
        Lines(outp).ShouldContain("memory rows pending: 0");
        Lines(outp).ShouldContain("code rows pending: 0");
        outp.ShouldContain("status: SHAPE MISMATCH");
    }

    /// <summary>R2 A9: a missing bank must not print any report line — the corpus reads stay behind the File.Exists guard.</summary>
    [RetryFact]
    public async Task Doctor_NoBank_PrintsNoReportLinesAtAll()
    {
        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        outp.ShouldNotContain("ai-raccoon doctor: ");
        outp.ShouldNotContain("status:");
    }

    /// <summary>R2 A9: an unresolvable encryption key must not print any report line either.</summary>
    [RetryFact]
    public async Task Doctor_KeyResolutionFails_PrintsNoReportLinesAtAll()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
        }

        var (_, outp, _) = await Run(CreateDoctor(new ThrowingKeyResolver()), ["doctor"]);

        outp.ShouldNotContain("ai-raccoon doctor: ");
        outp.ShouldNotContain("status:");
    }

    [RetryFact]
    public async Task Doctor_ConfiguredCodeEngine_NamesTheModelAndItsDirectory()
    {
        var modelDir = Path.Combine(_dataRoot, "models", "faxenoff__code-daemon-embed-v1");
        Directory.CreateDirectory(modelDir);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeModel, value = modelDir },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        // Whole line, label and value together: a keys-swap would print the directory on the
        // memory line and keep two separate substrings green (P3 §4.2).
        outp.ShouldContain($"code engine: {Path.GetFileName(modelDir)} (manifest unreadable) ({modelDir})");
        outp.ShouldNotContain("code engine: not configured");
    }

    /// <summary>
    ///     The count is the whole point of reporting it: rows sit `pending` forever with no engine,
    ///     and that is legitimate rather than an error, so nothing else in the product ever mentions
    ///     them. A number here is how someone learns the corpus is waiting on a model.
    /// </summary>
    [RetryFact]
    public async Task Doctor_ReportsHowManyCodeRowsArePending()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                VALUES ('h1', 'src/A.cs', 'a', 'src/A.cs', 1, 2, 'acme', 1, 1),
                       ('h2', 'src/B.cs', 'b', 'src/B.cs', 1, 2, 'acme', 1, 1)
                """, cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        outp.ShouldContain("code rows pending: 2");
    }

    /// <summary>
    ///     #522: `settings model threads &lt;n&gt;` round-trips through the bank, but doctor said
    ///     nothing about what it resolves to. Mirrors the code-engine line's shape.
    /// </summary>
    [RetryFact]
    public async Task Doctor_ExplicitThreadsSetting_ReportsTheResolvedCountAndSource()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.Threads, value = "3" },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        outp.ShouldContain("embedding threads: 3 (setting)");
    }

    [RetryFact]
    public async Task Doctor_UnsetThreadsSetting_ReportsTheHalvedCoreDefault()
    {
        // Opening once creates the bank; no threads setting is written.
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        var expected = Math.Max(1, Environment.ProcessorCount / 2);
        outp.ShouldContain($"embedding threads: {expected} (halved-core default)");
    }

    /// <summary>#522 review: 0 is a real setting meaning "ORT's own default", not zero threads — a bare "0" misreads as broken.</summary>
    [RetryFact]
    public async Task Doctor_ZeroThreadsSetting_ReportsOrtDefault()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.Threads, value = "0" },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        outp.ShouldContain("embedding threads: ORT default (setting)");
    }

    /// <summary>The exact GH #357 repro: an `entries` table already exists with a narrower shape before doctor ever touches it.</summary>
    [RetryFact]
    public async Task Doctor_HandSurgeredEntriesTable_DetectsShapeMismatchAndFailsDistinctExitCode()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = raw.CreateCommand();
            create.CommandText = "CREATE TABLE entries (only_one_column TEXT)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var beforeHash = FileSha256(_factory.BankPath);

        var (exit, outp, err) = await Run(CreateDoctor(), ["doctor"]);

        // The narrow table must survive the check byte-for-byte: a mutating open (OpenBankAsync)
        // would try to migrate/"heal" this exact bank instead of only reporting on it. Asserted
        // before the exit-code/content checks below so a mutation is never masked by an earlier failure.
        FileSha256(_factory.BankPath).ShouldBe(beforeHash, "doctor must never write to a bank it is diagnosing, healthy or not");

        exit.ShouldBe(ExitCode.SchemaVerificationFailed);
        exit.ShouldNotBe(ExitCode.FailedToResolveEncryptionKey);
        exit.ShouldNotBe(ExitCode.FailedToOpenEncryptedBank);
        var combined = outp + err;
        combined.ShouldContain("  - entries: missing column");
        combined.ShouldContain("missing column");
        combined.ShouldContain("remedy: start the server");
    }

    [RetryFact]
    public async Task Doctor_NeverModifiesTheBank()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var beforeHash = FileSha256(_factory.BankPath);
        var (beforeVersion, beforeAppId) = await ReadHeaderAsync();

        var (exit, _, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(0);
        var afterHash = FileSha256(_factory.BankPath);
        var (afterVersion, afterAppId) = await ReadHeaderAsync();

        afterHash.ShouldBe(beforeHash, "doctor must never write to the bank it is inspecting");
        afterVersion.ShouldBe(beforeVersion);
        afterAppId.ShouldBe(beforeAppId);
    }

    [RetryFact]
    public async Task Doctor_WhenTheEncryptionKeyCannotBeResolved_ReportsADistinctExitCode()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
        await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
        }

        var (exit, _, err) = await Run(CreateDoctor(new ThrowingKeyResolver()), ["doctor"]);

        exit.ShouldBe(ExitCode.FailedToResolveEncryptionKey);
        exit.ShouldNotBe(ExitCode.SchemaVerificationFailed);
        err.ShouldContain("encryption key");
    }

    private async Task<(long Version, int AppId)> ReadHeaderAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = "PRAGMA user_version";
        var version = (long)(await versionCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        await using var appIdCmd = connection.CreateCommand();
        appIdCmd.CommandText = "PRAGMA application_id";
        var appId = (int)(long)(await appIdCmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        return (version, appId);
    }

    /// <summary>R3: every report label the shared component exposes appears exactly once — a corpus block emitted twice, or one corpus block missing entirely, goes RED.</summary>
    [RetryFact]
    public async Task Doctor_EveryReportLabel_AppearsExactlyOnce()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);
        var lines = Lines(outp);

        foreach (var probe in CorpusEngineProbe.All)
        {
            lines.Count(l => l.StartsWith($"{probe.Label} engine: ", StringComparison.Ordinal)).ShouldBe(1, probe.Label);
            lines.Count(l => l.StartsWith($"{probe.Label} rows pending: ", StringComparison.Ordinal)).ShouldBe(1, probe.Label);
        }

        lines.Count(l => l.StartsWith("model migration: ", StringComparison.Ordinal)).ShouldBe(1);
        lines.Count(l => l.StartsWith("embedding threads: ", StringComparison.Ordinal)).ShouldBe(1);
    }

    /// <summary>R2 A7: absent and closed are deliberately the same settled state — a settled bank must never read as broken.</summary>
    [RetryTheory]
    [InlineData(null)]
    [InlineData(1787739482L)]
    public async Task Doctor_NoMigrationRowOrAClosedOne_ReportsTheSameSettledState(long? finishedAt)
    {
        if (finishedAt is null)
        {
            await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
            {
            }
        }
        else
        {
            await SeedMigrationAsync(finishedAt);
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain("model migration: none open");
        exit.ShouldBe(ExitCode.Success);
    }

    /// <summary>P1 Decision D: an absent model_migration table is a guard trip — `unreadable`, never 24, and the schema verdict (19) decides the exit.</summary>
    [RetryFact]
    public async Task Doctor_MissingModelMigrationTable_ReportsTheMigrationAsUnreadable()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DROP TABLE model_migration", cancellationToken: TestContext.Current.CancellationToken));
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        Lines(outp).ShouldContain("model migration: unreadable");
        exit.ShouldBe(ExitCode.SchemaVerificationFailed);          // the missing table IS the shape defect
        exit.ShouldNotBe(ExitCode.Success);
    }

    /// <summary>
    ///     R2 §6.6: a malformed row (TEXT in an INTEGER column) fails the Dapper mapping with
    ///     InvalidCastException — not a SqliteException — so the migration read degrades on any
    ///     non-cancellation failure. Also Decision D's witness: a guard-tripped read can never
    ///     produce the migration exit code, and every other line still prints.
    /// </summary>
    [RetryFact]
    public async Task Doctor_MalformedMigrationRow_ReportsUnreadable_AndExitsSuccess()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO model_migration (id, provider, model, base_url, engine, started_at, finished_at)
                VALUES (1, 'local', NULL, NULL, 'test-engine', 'not-a-number', NULL)
                """, cancellationToken: TestContext.Current.CancellationToken));
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.Success);                          // the schema is healthy; the row is not
        Lines(outp).ShouldContain("model migration: unreadable");
        Lines(outp).ShouldContain("memory rows pending: 0");
        Lines(outp).ShouldContain("status: HEALTHY");
    }

    [RetryFact]
    public async Task Doctor_NeverModifiesTheBank_WithBothCorporaAndAnOpenMigration()
    {
        var modelDir = Path.Combine(_dataRoot, "models", "memory-engine-dir");
        Directory.CreateDirectory(modelDir);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            foreach (var (key, value) in new[]
                     {
                         (EmbeddingSettingsKeys.Provider, "local"),
                         (EmbeddingSettingsKeys.Model, modelDir),
                         (EmbeddingSettingsKeys.CodeModel, modelDir)
                     })
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                    new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, value, project_id, scope, created_at, updated_at, embed_state)
                VALUES ('m1', 'one', 'acme', 'project', 0, 0, 'pending')
                """, cancellationToken: TestContext.Current.CancellationToken));
            await SeedMigrationAsync(finishedAt: null, connection);
        }

        var beforeHash = FileSha256(_factory.BankPath);
        var (beforeVersion, beforeAppId) = await ReadHeaderAsync();

        var (_, _, _) = await Run(CreateDoctor(), ["doctor"]);

        var afterHash = FileSha256(_factory.BankPath);
        var (afterVersion, afterAppId) = await ReadHeaderAsync();

        afterHash.ShouldBe(beforeHash, "doctor must never write to the bank it is inspecting, open migration or not");
        afterVersion.ShouldBe(beforeVersion);
        afterAppId.ShouldBe(beforeAppId);
    }

    /// <summary>
    ///     R2 B3/J4 + N5: the open outbox row — the exact live-bank defect — reported with its
    ///     started_at rendered as UTC, the pending count it caused, the new status word and the
    ///     new exit code all in ONE test, so the status arm and the exit constant pin each other.
    /// </summary>
    [RetryFact]
    public async Task Doctor_OpenMigration_ReportsItWithItsTimestamp_AndExits24()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, value, project_id, scope, created_at, updated_at, embed_state)
                VALUES ('m1', 'one', 'acme', 'project', 0, 0, 'embedded'),
                       ('m2', 'two', 'acme', 'project', 0, 0, 'embedded'),
                       ('m3', 'three', 'acme', 'project', 0, 0, 'embedded')
                """, cancellationToken: TestContext.Current.CancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.MarkAllEmbeddedPending,
                cancellationToken: TestContext.Current.CancellationToken));
            await SeedMigrationAsync(finishedAt: null, connection);
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.ModelMigrationOpen);
        exit.ShouldNotBe(ExitCode.Success);
        Lines(outp).ShouldContain("memory rows pending: 3");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1787739481).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Lines(outp).ShouldContain(
            $"model migration: open since {timestamp} (all MCP tool calls are refused until it finishes)");
        Lines(outp).ShouldContain(
            "status: MIGRATION IN PROGRESS (schema shape is healthy; MCP tool calls are refused until the re-embed finishes)");
        Lines(outp).ShouldNotContain("status: HEALTHY");
    }

    /// <summary>R2 B3: Decision C — an open migration on a shape-broken bank still exits 19, never 24; the schema verdict outranks the advisory code.</summary>
    [RetryFact]
    public async Task Doctor_OpenMigrationOnAShapeBrokenBank_StillExits19()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DROP TABLE maintenance_jobs", cancellationToken: TestContext.Current.CancellationToken));
            await SeedMigrationAsync(finishedAt: null, connection);
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.SchemaVerificationFailed);
        exit.ShouldNotBe(ExitCode.ModelMigrationOpen);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1787739481).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Lines(outp).ShouldContain(
            $"model migration: open since {timestamp} (all MCP tool calls are refused until it finishes)");
        outp.ShouldContain("status: SHAPE MISMATCH");
    }

    /// <summary>
    ///     P3 X3 (characterisation): the report does NOT distinguish a live drainer from a stale
    ///     one — the line depends on the row's started_at/finished_at only, never the lease, so
    ///     two equally-stuck banks print the same line.
    /// </summary>
    [RetryTheory]
    [InlineData("drainer-1", 1787739481L + 3600)]    // a live lease, still in the future
    [InlineData("drainer-1", 1787739481L - 3600)]    // the owner's state: an expired lease
    public async Task Doctor_OpenMigrationWithAnyLease_ReportsTheSameOpenState(string leaseOwner, long leaseExpiresAt)
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await SeedMigrationAsync(finishedAt: null, connection, leaseOwner, leaseExpiresAt);
        }

        var (exit, outp, _) = await Run(CreateDoctor(), ["doctor"]);

        exit.ShouldBe(ExitCode.ModelMigrationOpen);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(1787739481).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        Lines(outp).ShouldContain(
            $"model migration: open since {timestamp} (all MCP tool calls are refused until it finishes)");
        outp.ShouldNotContain(leaseOwner);
    }

    private async Task SeedMigrationAsync(long? finishedAt, SqliteConnection? connection = null, string? leaseOwner = null, long? leaseExpiresAt = null, long startedAt = 1787739481)
    {
        await using var own = connection ?? await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await own.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO model_migration (id, provider, model, base_url, engine, started_at, finished_at, lease_owner, lease_expires_at)
            VALUES (1, 'local', NULL, NULL, 'test-engine', @startedAt, @finishedAt, @leaseOwner, @leaseExpiresAt)
            ON CONFLICT(id) DO UPDATE SET finished_at = @finishedAt, lease_owner = @leaseOwner, lease_expires_at = @leaseExpiresAt
            """, new { startedAt, finishedAt, leaseOwner, leaseExpiresAt },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private static string[] Lines(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];

    private static string FileSha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class ThrowingKeyResolver : IEncryptionKeyResolver
    {
        public Task<ResolvedKey> ResolveAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("simulated encryption key resolution failure");
    }
}
