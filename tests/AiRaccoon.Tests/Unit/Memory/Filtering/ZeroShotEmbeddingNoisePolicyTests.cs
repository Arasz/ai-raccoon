using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait("Speed", "Fast")]
public class ZeroShotEmbeddingNoisePolicyTests
{
    [Fact]
    public async Task EvaluateAsync_WhenContentMatchesNoiseVector_ReturnsNoise()
    {
        // Arrange
        var fakeEmbedder = new FakeEmbedder(new float[] { 1.0f, 0.0f, 0.0f });
        var fakeNoiseProvider = new FakeNoiseVectorProvider(new List<NoiseVector>
        {
            new("terminal_completion_log", new float[] { 0.95f, 0.05f, 0.0f })
        });

        var policy = new ZeroShotEmbeddingNoisePolicy(fakeEmbedder, fakeNoiseProvider);
        var request = new MemoryWriteRequest("proj-1", "[IMPORTANT: Background process proc_123 completed normally...]");

        // Act
        var result = await policy.EvaluateAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.IsNoise.ShouldBeTrue();
        result.PolicyName.ShouldBe("ZeroShotSemanticNoiseFilter");
    }

    [Fact]
    public async Task EvaluateAsync_WhenContentIsOrthogonalToNoise_ReturnsClean()
    {
        // Arrange
        var fakeEmbedder = new FakeEmbedder(new float[] { 0.0f, 1.0f, 0.0f });
        var fakeNoiseProvider = new FakeNoiseVectorProvider(new List<NoiseVector>
        {
            new("terminal_completion_log", new float[] { 1.0f, 0.0f, 0.0f })
        });

        var policy = new ZeroShotEmbeddingNoisePolicy(fakeEmbedder, fakeNoiseProvider);
        var request = new MemoryWriteRequest("proj-1", "This is important architectural documentation about C# memory store.");

        // Act
        var result = await policy.EvaluateAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.IsNoise.ShouldBeFalse();
    }

    private class FakeEmbedder : IEntryEmbedder
    {
        private readonly float[] _stubVector;

        public FakeEmbedder(float[] stubVector)
        {
            _stubVector = stubVector;
        }

        public Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<float[]?>(_stubVector);
        }

        // Unused interface stubs
        public Task<EmbeddingConfig> ConfigureAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string provider, string? model, string? baseUrl, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task EmbedIfConfiguredAsync(Microsoft.Data.Sqlite.SqliteConnection connection, long id, string value, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<int> EmbedPendingAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string projectId, int? limit, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<byte[]?> EmbedQueryAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string query, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<EmbeddingSettings> ReadSettingsAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private class FakeNoiseVectorProvider : INoiseVectorProvider
    {
        private readonly IReadOnlyList<NoiseVector> _vectors;

        public FakeNoiseVectorProvider(IReadOnlyList<NoiseVector> vectors)
        {
            _vectors = vectors;
        }

        public Task<IReadOnlyList<NoiseVector>> GetNoiseVectorsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_vectors);
        }
    }
}
