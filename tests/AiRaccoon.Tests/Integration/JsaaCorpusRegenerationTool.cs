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
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Regenerates tests/AiRaccoon.Tests/Resources/jsaa-memory.db through the production
///     <see cref="AiRaccoon.Infrastructure.Ingestion.FileIngestor" /> — not the vendored Python
///     re-chunker (docs/plans/2026-08-14-code-quality-improvement-plan.md WP4). Local-only:
///     it reads a second checkout on this machine (job-search-ai-assistant) and overwrites a
///     committed binary fixture, so CI has no such input and the test skips.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class JsaaCorpusRegenerationTool(ITestOutputHelper output)
{
    private const string RunEnvVar = "AIRACCOON_REGENERATE_JSAA_CORPUS";
    private const string JsaaRootEnvVar = "AIRACCOON_JSAA_ROOT";
    private const string DefaultJsaaRoot = "/Users/arasz/RiderProjects/job-search-ai-assistant";

    /// <summary>The corpus pin (scripts/src/jsaa_config.py JSAA_PINNED_COMMIT) — kept identical
    /// here so a drifted pin fails loudly instead of silently ingesting the wrong tree.</summary>
    private const string PinnedCommit = "9397bbef504b5b30a31003c84e8c5c316641adb6";

    [Fact]
    public async Task RegenerateJsaaMemoryDb()
    {
        if (Environment.GetEnvironmentVariable(RunEnvVar) != "1")
        {
            Assert.Skip($"{RunEnvVar} not set — this tool overwrites the committed " +
                        "tests/AiRaccoon.Tests/Resources/jsaa-memory.db fixture from a second, " +
                        $"local checkout of job-search-ai-assistant pinned to {PinnedCommit}. " +
                        $"Set {RunEnvVar}=1 (and optionally {JsaaRootEnvVar}) to run it.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var jsaaRoot = Environment.GetEnvironmentVariable(JsaaRootEnvVar) ?? DefaultJsaaRoot;
        Directory.Exists(jsaaRoot).ShouldBeTrue($"jsaa root not found: {jsaaRoot}");

        var extractRoot = TestData.CreateTempRoot("ai-raccoon-jsaa-corpus-extract");
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-jsaa-corpus-bank");
        try
        {
            ExtractPinnedCommit(jsaaRoot, PinnedCommit, extractRoot);
            var (pin, projectId, files) = ListCorpusFiles(extractRoot);
            pin.ShouldBe(PinnedCommit, "list-jsaa-corpus-files.py resolved a different pin than expected");
            files.ShouldNotBeEmpty();
            output.WriteLine($"jsaa pin {pin}: {files.Count} curated files selected");

            var ensured = await TestData.CreateBundledModel().EnsureAsync(cancellationToken);
            ensured.AllPresent.ShouldBeTrue(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");

            var factory = new SqliteConnectionFactory(
                new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
                NullKeyProvider.Resolver(
                    new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
            var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
                new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(),
                new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)),
                TestData.CreateEmbeddingService());

            await TestData.ConfigureAndDrainEmbeddingAsync(store, factory, TestData.CreateEmbeddingService(),
                "local", null, null, cancellationToken);
            await store.SetSettingAsync(IngestScopeKeys.ScopeProject(projectId),
                IngestScopeKeys.Serialize([extractRoot]), cancellationToken);

            var ingestedFiles = 0;
            var indexedChunks = 0;

            // IngestFileAsync stores the path argument verbatim as source_file/path and hashes
            // from it (FileIngestor.InsertChunksAsync), so passing the extractRoot-joined
            // absolute path would bake this machine's temp directory into the committed fixture.
            // Passing the repo-relative `rel` instead keeps the stored paths clean; the process's
            // current directory is set to extractRoot so File.ReadAllTextAsync(rel) still resolves
            // and RequireInScopeAsync's IngestPath.Normalize (which calls Path.GetFullPath, itself
            // CWD-relative) still resolves `rel` back to an absolute path under the configured
            // scope. Directory.SetCurrentDirectory is process-global, which is only acceptable
            // because this tool is env-gated (AIRACCOON_REGENERATE_JSAA_CORPUS=1) and runs alone.
            var originalDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(extractRoot);
            try
            {
                foreach (var rel in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var indexed = await store.IngestFileAsync(projectId, rel, null, cancellationToken);
                    indexedChunks += indexed;
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

            output.WriteLine($"ingested {ingestedFiles}/{files.Count} files (0-chunk files are non-indexable " +
                             "extensions under the curated globs, e.g. skill reference assets)");

            var pending = await store.EmbedPendingAsync(projectId, null, cancellationToken);
            output.WriteLine($"final embed-pending pass: processed={pending.Processed} remaining={pending.Pending}");
            pending.Pending.ShouldBe(0, "every row must finish embedded — a bundled-model engine never leaves rows pending");

            var repoRoot = Path.GetDirectoryName(TestData.RepoFile("AiRaccoon.slnx"))!;
            var targetPath = Path.Combine(repoRoot, "tests", "AiRaccoon.Tests", "Resources", "jsaa-memory.db");
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

            output.WriteLine($"wrote {targetPath}");

            var (total, withStructure, distinctHeadings) = await CountStructureAsync(targetPath, cancellationToken);
            output.WriteLine($"regenerated corpus: {total} rows, {withStructure} with structure_embedding, " +
                             $"{distinctHeadings} distinct heading paths");
            total.ShouldBeGreaterThan(0);
            withStructure.ShouldBeGreaterThan(0, "at least one chunk must carry an H1/H2 to exercise the structure modality");
        }
        finally
        {
            SafeDelete(extractRoot);
            SafeDelete(dataRoot);
        }
    }

    private static void ExtractPinnedCommit(string jsaaRoot, string pin, string extractRoot)
    {
        RunProcessOrThrow(jsaaRoot, "git", $"archive {pin}", extractRoot, pipeToTar: true);
    }

    private static (string Pin, string ProjectId, IReadOnlyList<string> Files) ListCorpusFiles(string extractRoot)
    {
        var scriptPath = TestData.RepoFile("scripts/list-jsaa-corpus-files.py");
        var stdout = RunProcessOrThrow(Path.GetDirectoryName(scriptPath)!, "python3",
            $"\"{scriptPath}\" \"{extractRoot}\"", null, pipeToTar: false);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var pin = root.GetProperty("pinnedCommit").GetString()!;
        var projectId = root.GetProperty("projectId").GetString()!;
        var files = root.GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToList();
        return (pin, projectId, files);
    }

    /// <summary>Runs a command; when <paramref name="pipeToTar" /> is set, pipes its stdout into
    /// `tar -x -C untarDestination` (used for `git archive | tar -x`). Returns captured stdout.</summary>
    private static string RunProcessOrThrow(string workingDirectory, string fileName, string arguments,
        string? untarDestination, bool pipeToTar)
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

        string stdout;
        if (pipeToTar)
        {
            using var tar = new Process
            {
                StartInfo = new ProcessStartInfo("tar", $"-x -C \"{untarDestination}\"")
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            tar.Start();
            process.StandardOutput.BaseStream.CopyTo(tar.StandardInput.BaseStream);
            tar.StandardInput.Close();
            var tarError = tar.StandardError.ReadToEnd();
            tar.WaitForExit();
            var srcError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || tar.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{fileName} {arguments} | tar failed: git-exit={process.ExitCode} tar-exit={tar.ExitCode}\n{srcError}\n{tarError}");
            }

            stdout = "";
        }
        else
        {
            stdout = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{fileName} {arguments} failed (exit {process.ExitCode}): {error}");
            }
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
