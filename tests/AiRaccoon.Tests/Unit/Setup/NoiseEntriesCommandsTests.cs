using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Criterion 3 (this task): a CLI verb reads/summarizes noise_entries. Without a reader, ADR-0029's
///     "high-quality dataset of true negatives" is a write-only log again — this pins the read path
///     against a real scratch bank.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class NoiseEntriesCommandsTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("noise-entries-cli");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteNoiseEntryStore _store;

    public NoiseEntriesCommandsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteNoiseEntryStore(_factory);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private Task<(int Exit, string Out, string Err)> Run(string[] args) =>
        CliRun.RunAsync(args, TestData.CreateConfigCommands(new FakeConfigStore(), noiseEntries: new NoiseEntriesCommands(_store)));

    [Fact]
    public async Task NoiseEntries_EmptyBank_ReportsZero()
    {
        var (exit, outp, _) = await Run(["settings", "noise", "entries"]);

        exit.ShouldBe(0);
        outp.ShouldContain("total: 0");
    }

    [Fact]
    public async Task NoiseEntries_AfterRecording_ReportsCountsByPolicy()
    {
        await _store.RecordAsync(new MemoryWriteRequest("proj-1", "[IMPORTANT: Background process x completed normally]"),
            "HermesBackgroundProcessLog", expiresAtUnixSeconds: 2_000, nowUnixSeconds: 1_000, TestContext.Current.CancellationToken);
        await _store.RecordAsync(new MemoryWriteRequest("proj-1", "[ASYNC DELEGATION BATCH COMPLETE]"),
            "HermesBackgroundProcessLog", expiresAtUnixSeconds: 2_000, nowUnixSeconds: 1_000, TestContext.Current.CancellationToken);

        var (exit, outp, _) = await Run(["settings", "noise", "entries"]);

        exit.ShouldBe(0);
        outp.ShouldContain("total: 2");
        outp.ShouldContain("HermesBackgroundProcessLog: 2");
    }
}
