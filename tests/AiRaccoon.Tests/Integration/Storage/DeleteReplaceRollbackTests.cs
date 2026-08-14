using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     QA-F2 (docs/reviews/2026-08-14-moe-codebase-review.md): the <c>catch { ROLLBACK; throw; }</c>
///     branch in <c>DeleteSourcePathAsync</c> and <c>ReplaceFileAsync</c> was never exercised. These
///     tests force a real statement failure partway through each method's transaction — a trigger that
///     raises, not a production seam — and assert the bank is left exactly as it was before the call.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DeleteReplaceRollbackTests : IDisposable
{
    private const string InducedFailureMessage = "induced failure for rollback test";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-rollback-tests");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public DeleteReplaceRollbackTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory),
            TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task DeleteSourcePath_WhenTheWatchFingerprintDeleteFailsMidTransaction_RollsBackTheEntryDeleteToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "rollback-delete.md");
        await File.WriteAllTextAsync(file, "quaquaversal rollback delete content", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);
        await _store.IngestFileAsync("acme", file, null, ct);
        await using (var setup = await _factory.OpenBankAsync(ct))
        {
            // A real fingerprint row, so the second statement (DeleteWatchFilesByProjectPathCascade)
            // actually attempts to delete a row and the trigger below has something to fire on.
            await setup.ExecuteAsync(
                "INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES (@projectId, @path, @now, @now)",
                new { projectId = "acme", path = file, now = 1L });
            await setup.ExecuteAsync(
                "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES (@projectId, @path, @hash, @now)",
                new { projectId = "acme", path = file, hash = "original-hash", now = 1L });
        }

        var preEntries = await CountEntriesAsync("acme", ct);
        preEntries.ShouldBeGreaterThan(0, "the file must actually be ingested before the forced-failure call");

        await InstallFailureTriggerAsync("DELETE", "watch_files", ct);
        try
        {
            var thrown = await Should.ThrowAsync<SqliteException>(
                () => _store.DeleteSourcePathAsync("acme", file, ct));
            thrown.Message.ShouldContain(InducedFailureMessage);
        }
        finally
        {
            await DropFailureTriggerAsync(ct);
        }

        (await CountEntriesAsync("acme", ct)).ShouldBe(preEntries,
            "the entry delete (statement 1) must have been rolled back along with the failed watch_files delete (statement 2)");
        await using var verify = await _factory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<string>(
                "SELECT file_hash FROM watch_files WHERE project_id = @projectId AND path = @path",
                new { projectId = "acme", path = file }))
            .ShouldBe("original-hash", "the watch fingerprint itself must be unchanged — the failed statement's own effect was rolled back too");
    }

    [Fact]
    public async Task ReplaceFile_WhenTheWatchFingerprintUpsertFailsMidTransaction_RollsBackTheWholeReplace()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "rollback-replace.md");
        await File.WriteAllTextAsync(file, "gallimaufry original replace content", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);
        await _store.IngestFileAsync("acme", file, null, ct);
        // No watch_files row exists yet, so UpsertWatchFile's ON CONFLICT upsert takes the plain
        // INSERT branch, and a BEFORE INSERT trigger is guaranteed to fire.
        var preEntries = await CountEntriesAsync("acme", ct);
        preEntries.ShouldBeGreaterThan(0, "the file must actually be ingested before the forced-failure call");

        // Rewrite the file on disk so a successful replace would swap the content — proves the test
        // would have detected a real (non-rolled-back) replace, not just a no-op.
        await File.WriteAllTextAsync(file, "gallimaufry revised replace content", ct);

        await InstallFailureTriggerAsync("INSERT", "watch_files", ct);
        try
        {
            var thrown = await Should.ThrowAsync<SqliteException>(
                () => _store.ReplaceFileAsync("acme", file, "revised-hash", ct));
            thrown.Message.ShouldContain(InducedFailureMessage);
        }
        finally
        {
            await DropFailureTriggerAsync(ct);
        }

        (await CountEntriesAsync("acme", ct)).ShouldBe(preEntries,
            "the delete-and-reingest must have been rolled back — the row count must be exactly what it was before the failed replace");
        (await _store.SearchAsync(new SearchQuery("acme", "gallimaufry original"), ct))
            .ShouldNotBeEmpty("the original content must still be there — the replace never committed");
        // Raw SQL, not _store.SearchAsync: hybrid (FTS + vector) search can surface a semantically
        // near result even for text that was never written, so the direct row content is the only
        // precise proof that the revised value never landed.
        await using var verify = await _factory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM entries WHERE project_id = @projectId AND value LIKE '%revised%'",
                new { projectId = "acme" }))
            .ShouldBe(0, "the revised content must not have been committed");
        (await verify.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM watch_files WHERE project_id = @projectId AND path = @path",
                new { projectId = "acme", path = file }))
            .ShouldBe(0, "the aborted watch_files insert must not have landed either");
    }

    private async Task<int> CountEntriesAsync(string projectId, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM entries WHERE project_id = @projectId", new { projectId });
    }

    /// <summary>
    ///     A real (non-TEMP) trigger, so it is visible to whatever new connection the store's own
    ///     factory opens next — not a production seam, just DDL a test installs and removes on the
    ///     shared bank file. Fires on the given operation against the given table and aborts just
    ///     that statement (RAISE(ABORT, ...) backs out the failed statement's own effect but leaves
    ///     the surrounding BEGIN IMMEDIATE transaction open for the production code's own ROLLBACK).
    /// </summary>
    private async Task InstallFailureTriggerAsync(string operation, string table, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync($"""
            CREATE TRIGGER test_force_fail_{operation.ToLowerInvariant()}_{table}
            BEFORE {operation} ON {table}
            FOR EACH ROW
            BEGIN
                SELECT RAISE(ABORT, '{InducedFailureMessage}');
            END;
            """);
    }

    private async Task DropFailureTriggerAsync(CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        var names = await connection.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'test_force_fail_%'");
        foreach (var name in names)
        {
            await connection.ExecuteAsync($"DROP TRIGGER {name}");
        }
    }
}
