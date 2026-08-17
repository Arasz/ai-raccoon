using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     `repair reingest` discards per-row metadata as a consequence of the re-chunk, not a hazard to
///     work around (owner ruling) — so an operator must see that stated before AND after --apply,
///     never only in one of the two. No gate proves preservation; none is intended.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ReingestRepairCommandsTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("reingest-repair-cli");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public ReingestRepairCommandsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task RunAsync_DryRun_WarnsAboutMetadataLoss()
    {
        await SeedStaleFileAsync();

        var stdout = await RunAsync(apply: false);

        stdout.ShouldContain("metadata", Case.Insensitive);
        stdout.ShouldContain("rating");
        stdout.ShouldContain("discarded");
    }

    [Fact]
    public async Task RunAsync_Apply_WarnsAboutMetadataLoss()
    {
        await SeedStaleFileAsync();

        var stdout = await RunAsync(apply: true);

        stdout.ShouldContain("metadata", Case.Insensitive);
        stdout.ShouldContain("rating");
        stdout.ShouldContain("discarded");
    }

    private async Task SeedStaleFileAsync()
    {
        var file = Path.Combine(_dataRoot, "stale.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);
        await _store.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var id = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM entries WHERE source_file = @file ORDER BY id LIMIT 1", new { file });
        await connection.ExecuteAsync(
            "UPDATE entries SET hash = @staleHash, chunk_index = -1 WHERE id = @id",
            new { staleHash = "stale-" + Guid.NewGuid().ToString("N"), id });
    }

    private async Task<string> RunAsync(bool apply)
    {
        var argv = apply ? new[] { "repair", "reingest", "--apply" } : ["repair", "reingest"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]);
        var command = new ReingestRepairCommands(_factory, matcher, _store);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await command.RunAsync(parsed!.ParsedCliArgs, new StandardStreams(TextReader.Null, stdout, stderr),
            TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }
}
