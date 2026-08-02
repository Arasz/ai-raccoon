using AiRaccon.Core.Common;
using AiRaccon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Domain;

public class MemoryStorePortTests
{
    [Fact]
    public async Task ShareAsync_IsPartOfThePort_AndReturnsTheSharedEntry()
    {
        var store = new RecordingStore();

        var entry = await store.ShareAsync("acme", "abc123", TestContext.Current.CancellationToken);

        entry.Context.ShouldBe(ContextNaming.SharedContext);
        store.Shared.ShouldBe(("acme", "abc123"));
    }

    private sealed class RecordingStore : IMemoryStore
    {
        public (string ProjectId, string Hash)? Shared { get; private set; }

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default)
        {
            Shared = (projectId, hash);
            return Task.FromResult(new MemoryEntry(hash, "notes.md", ContextNaming.SharedContext, "value", 1));
        }
    }
}
