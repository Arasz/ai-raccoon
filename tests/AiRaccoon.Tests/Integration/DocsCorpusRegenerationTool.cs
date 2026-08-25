using System.Diagnostics;
using System.Text.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Regenerates tests/AiRaccoon.Tests/Resources/docs-memory.db from this repository's own
///     public documentation through the production FileIngestor (ADR-0042, ADR-0090). The corpus
///     is the tree this test runs in — no second checkout, no pinned foreign commit. Env-gated
///     because it overwrites a committed binary, and arm64-only because corpus vectors are baked
///     once on one arithmetic path (ADR-0049/0050).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DocsCorpusRegenerationTool(ITestOutputHelper output)
{
    private const string RunEnvVar = "AIRACCOON_REGENERATE_DOCS_CORPUS";

    [RetryFact]
    public async Task RegenerateDocsMemoryDb()
    {
        if (Environment.GetEnvironmentVariable(RunEnvVar) != "1")
        {
            Assert.Skip($"{RunEnvVar} not set — this tool overwrites the committed " +
                        "tests/AiRaccoon.Tests/Resources/docs-memory.db fixture from this repository's " +
                        $"own docs. Set {RunEnvVar}=1 on an arm64 host to run it (ADR-0049/0050).");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var repoRoot = Path.GetDirectoryName(TestData.RepoFile("AiRaccoon.slnx"))!;
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-docs-corpus-bank");
        try
        {
            var (projectId, files) = ListCorpusFiles(repoRoot);
            files.ShouldNotBeEmpty();
            output.WriteLine($"corpus root {repoRoot}: {files.Count} curated files selected " +
                             $"for project '{projectId}'");

            var ensured = await TestData.CreateBundledModel().EnsureAsync(cancellationToken);
            ensured.AllPresent.ShouldBeTrue(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");

            var factory = new SqliteConnectionFactory(
                new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
                NullKeyProvider.Resolver(
                    new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero));
            var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
                new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(),
                clock, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

            await TestData.ConfigureAndDrainEmbeddingAsync(store, factory, TestData.CreateEmbeddingService(),
                "local", null, null, cancellationToken, clock);
            await store.SetSettingAsync(IngestScopeKeys.ScopeProject(projectId),
                IngestScopeKeys.Serialize([repoRoot]), cancellationToken);

            // Provenance: which commit of this repo the committed bank was built from. Recorded
            // in the bank itself so a stale fixture can be dated without guessing from git log.
            await store.SetSettingAsync("corpus.source_commit", HeadCommit(repoRoot), cancellationToken);
            await store.SetSettingAsync("corpus.source_repo", "ai-raccoon", cancellationToken);

            var ingestedFiles = 0;

            // IngestFileAsync stores the path argument verbatim as source_file/path and hashes
            // from it, so passing an absolute path would bake THIS machine's checkout location
            // into the committed fixture — and a worktree path at that. Passing the repo-relative
            // `rel` keeps stored paths portable; the process's current directory is set to the
            // repo root so File.ReadAllTextAsync(rel) still resolves and RequireInScopeAsync's
            // IngestPath.Normalize (Path.GetFullPath, itself CWD-relative) still resolves `rel`
            // back under the configured scope. Directory.SetCurrentDirectory is process-global,
            // which is only acceptable because this tool is env-gated and runs alone.
            var originalDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(repoRoot);
            try
            {
                foreach (var rel in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // IngestFileAsync returns a replaced-file count, not a chunk count — the
                    // real chunk total is read back from the bank below.
                    var indexed = await store.IngestFileAsync(projectId, rel, null, cancellationToken);
                    if (indexed > 0)
                    {
                        ingestedFiles++;
                    }
                }
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDirectory);
            }

            output.WriteLine($"ingested {ingestedFiles}/{files.Count} files");

            var pending = await store.EmbedPendingAsync(projectId, null, cancellationToken);
            output.WriteLine($"final embed-pending pass: processed={pending.Processed} remaining={pending.Pending}");
            pending.Pending.ShouldBe(0, "every row must finish embedded — a bundled-model engine never leaves rows pending");

            var targetPath = Path.Combine(repoRoot, "tests", "AiRaccoon.Tests", "Resources", "docs-memory.db");
            await using (var connection = await factory.OpenBankAsync(cancellationToken))
            {
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                await using var vacuum = connection.CreateCommand();
                vacuum.CommandText = $"VACUUM INTO '{targetPath.Replace("'", "''")}'";
                await vacuum.ExecuteNonQueryAsync(cancellationToken);
            }

            var sizeBytes = new FileInfo(targetPath).Length;
            output.WriteLine($"wrote {targetPath} ({sizeBytes:N0} bytes, {sizeBytes / 1024.0 / 1024.0:F2} MiB)");

            var (total, withStructure, distinctHeadings) = await CountStructureAsync(targetPath, cancellationToken);
            output.WriteLine($"regenerated corpus: {total} rows, {withStructure} with structure_embedding, " +
                             $"{distinctHeadings} distinct heading paths");
            total.ShouldBeGreaterThan(0);
            withStructure.ShouldBeGreaterThan(0, "at least one chunk must carry an H1/H2 to exercise the structure modality");
        }
        finally
        {
            SafeDelete(dataRoot);
        }
    }

    private static string HeadCommit(string repoRoot) =>
        RunProcessOrThrow(repoRoot, "git", "rev-parse HEAD").Trim();

    private static (string ProjectId, IReadOnlyList<string> Files) ListCorpusFiles(string repoRoot)
    {
        var scriptPath = TestData.RepoFile("scripts/list-corpus-files.py");
        var stdout = RunProcessOrThrow(Path.GetDirectoryName(scriptPath)!, "python3",
            $"\"{scriptPath}\" \"{repoRoot}\"");
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var projectId = root.GetProperty("projectId").GetString()!;
        var files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToList();
        return (projectId, files);
    }

    private static string RunProcessOrThrow(string workingDirectory, string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} failed (exit {process.ExitCode}): {error}");
        }

        return stdout;
    }

    private static async Task<(int Total, int WithStructure, int DistinctHeadings)> CountStructureAsync(
        string dbPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*), sum(structure_embedding IS NOT NULL), count(DISTINCT heading_path) FROM entries";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.GetInt32(2));
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup; the OS temp dir gets swept eventually
        }
    }
}
