using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Extraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExtractionHostedServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore : IMemoryStore
    {
        public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

        public List<string> Projects { get; } = ["acme", "beta"];

        public Dictionary<string, List<ExtractionCandidateRow>> Candidates { get; } = new();

        public SharedIndex Index { get; set; } = new([], []);

        public List<(string ProjectId, string Hash)> Shared { get; } = [];

        public Exception? ExtractionError { get; set; }

        public int ExtractionCalls { get; private set; }

        public Exception? IntervalReadError { get; set; }

        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Projects);

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default)
        {
            ExtractionCalls++;
            if (ExtractionError is not null && projectId == "acme")
            {
                throw ExtractionError;
            }

            return Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>(
                Candidates.GetValueOrDefault(projectId) ?? []);
        }

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Index);

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            Shared.Add((projectId, hash));
            var row = Candidates.Values.SelectMany(x => x).First(r => r.Hash == hash);
            Index = new SharedIndex(
                Index.Values.Append(row.Value).ToArray(),
                Index.Paths.Append($"shared/{row.Path}").ToArray());
            return Task.FromResult(new MemoryEntry(hash, row.Path, ContextNaming.SharedContext, row.Value, 1));
        }

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            if (IntervalReadError is not null && key == ExtractionConfigKeys.IntervalMinutesGlobal)
            {
                throw IntervalReadError;
            }

            return Task.FromResult(Settings.GetValueOrDefault(key));
        }

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Settings
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!));

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            Settings.Remove(key);
            return Task.CompletedTask;
        }

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static (FakeStore Store, FakeTimeProvider Time, ExtractionHostedService Service) NewStack() =>
        NewStack(NullLogger<ExtractionHostedService>.Instance);

    private static (FakeStore Store, FakeTimeProvider Time, ExtractionHostedService Service) NewStack(
        ILogger<ExtractionHostedService> logger)
    {
        var store = new FakeStore();
        var time = new FakeTimeProvider(FixedNow);
        var service = new ExtractionHostedService(store, new SharedExtractionService(), time, logger);
        return (store, time, service);
    }

    private static ExtractionCandidateRow Row(string hash, string? sourceFile = null, string value = "fact",
        int accessCount = 0, double rating = 0.5) =>
        new(hash, $"{hash}.md", value, sourceFile, rating, accessCount, FixedNow.AddDays(-5), null);

    [Fact]
    public async Task RunOnce_Disabled_DoesNothing()
    {
        var (store, _, service) = NewStack();
        store.Candidates["acme"] = [Row("h1", sourceFile: null, value: "organic fact about beta")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBeEmpty();
        store.ExtractionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RunOnce_ProposeMode_ListsCandidates_WithoutSharing()
    {
        var (store, _, service) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Candidates["acme"] = [Row("h1", sourceFile: null, value: "organic fact about beta")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBeEmpty();
        store.ExtractionCalls.ShouldBe(2); // both projects were scanned; nothing shared
    }

    [Fact]
    public async Task RunOnce_PromoteMode_SharesRankedCandidates_WithDedup()
    {
        var (store, _, service) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] =
        [
            Row("h1", sourceFile: null, value: "organic fact about beta"),
            Row("h2", sourceFile: null, value: "already shared fact"),
            Row("h3", sourceFile: "docs/x.md")
        ];
        store.Index = new SharedIndex(["alreadysharedfact"], []);

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBe([("acme", "h1")]);
    }

    [Fact]
    public async Task RunOnce_PromoteMode_DedupsIdenticalContentAcrossProjectsInOnePass()
    {
        var (store, _, service) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        // Both projects hold the identical fact; the first promote must make the
        // second dedup against the refreshed shared index within the same pass.
        store.Candidates["acme"] = [Row("h1", sourceFile: null, value: "identical cross-project fact")];
        store.Candidates["beta"] = [Row("h1", sourceFile: null, value: "identical cross-project fact")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBe([("acme", "h1")]);
    }

    [Fact]
    public async Task RunOnce_ProjectError_IsTolerated_OthersStillProcessed()
    {
        var (store, _, service) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["beta"] = [Row("h9", sourceFile: null, value: "organic fact about acme")];
        store.ExtractionError = new InvalidOperationException("boom");

        await Should.NotThrowAsync(() =>
            service.RunOnceAsync(TestContext.Current.CancellationToken));

        store.Shared.ShouldBe([("beta", "h9")]);
    }

    [Fact]
    public async Task RunOnce_NoProjects_NoOp()
    {
        var (store, _, service) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Projects.Clear();

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Shared.ShouldBeEmpty();
        store.ExtractionCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_IntervalReadFailure_DoesNotKillTheLoop()
    {
        var (store, time, service) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", sourceFile: null, value: "organic fact about beta")];
        store.IntervalReadError = new InvalidOperationException("db busy");

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);

        // ExecuteAsync must fall back to the default interval and keep looping,
        // not fault (BackgroundService StopHost would kill the whole server).
        await Task.Delay(50, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        run.IsFaulted.ShouldBeFalse();
        store.ExtractionCalls.ShouldBeGreaterThan(0);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ExecuteAsync_PollLoop_RespectsInterval_AndStopsOnCancellation()
    {
        var (store, time, service) = NewStack();
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", sourceFile: null, value: "organic fact about beta")];

        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);

        // Let ExecuteAsync create the PeriodicTimer before advancing the fake clock.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(30)); // default interval
        await Task.Delay(100, TestContext.Current.CancellationToken);
        store.Shared.Count.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        // Second pass re-reads the shared index and dedups, so the share count stays 1;
        // the loop's iteration is proven by the extraction call count (2 passes x 2 projects).
        store.Shared.Count.ShouldBe(1);
        store.ExtractionCalls.ShouldBe(4);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunOnce_Cancelled_PropagatesOperationCanceled_WithoutProjectFailedLog()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", sourceFile: null, value: "organic fact about beta")];
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        store.ExtractionError = new OperationCanceledException(); // BARE — no token attached

        await Should.ThrowAsync<OperationCanceledException>(() => service.RunOnceAsync(cts.Token));

        store.ExtractionCalls.ShouldBe(1); // aborts at acme; beta never scanned
        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 503);
        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunOnce_UncancelledOce_IsTolerated_OthersStillProcessed()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["beta"] = [Row("h9", sourceFile: null, value: "organic fact about acme")];
        using var cts = new CancellationTokenSource(); // NOT cancelled
        store.ExtractionError = new OperationCanceledException("boom");

        await Should.NotThrowAsync(() => service.RunOnceAsync(cts.Token));

        store.Shared.ShouldBe([("beta", "h9")]);
        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 503 && r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task RunOnce_ProposeMode_LogsRankedCandidateDetails()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Candidates["acme"] =
        [
            Row("h1", sourceFile: null, value: "organic fact about beta"),   // organic-write + cross-project
            Row("h2", sourceFile: null, value: "another organic fact"),       // organic-write only
            Row("h3", sourceFile: "docs/x.md", value: "plain fact")           // below floor → excluded
        ];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        var records = logger.Collector.GetSnapshot().Where(r => r.Id.Id == 507).ToList();
        records.Count.ShouldBe(2);
        records.ShouldAllBe(r => r.Level == LogLevel.Information);
        records[0].Message.ShouldContain("#1");
        records[0].Message.ShouldContain("acme");
        records[0].Message.ShouldContain("h1.md");
        records[0].Message.ShouldContain("organic-write");
        records[0].Message.ShouldContain("cross-project");
        records[0].Message.ShouldContain("organic fact about beta");
        // Rank ordering: #1 before #2 in emission order (the list is pre-sorted by score).
        records[1].Message.ShouldContain("#2");
        // Counts line still emitted alongside the details.
        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 502);
    }

    [Fact]
    public async Task RunOnce_PromoteMode_LogsCountsOnly_NoCandidateDetails()
    {
        var logger = new FakeLogger<ExtractionHostedService>();
        var (store, _, service) = NewStack(logger);
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";
        store.Settings[ExtractionConfigKeys.ModeGlobal] = "promote";
        store.Candidates["acme"] = [Row("h1", sourceFile: null, value: "organic fact about beta")];

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 502);       // pass ran
        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 507);    // no candidate details
    }
}
