using AiRaccoon.Core;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests;

public static class TestData
{
    /// <summary>Serializes tests that mutate the process-global AIRACCOON_DB_PASSPHRASE (shared by CliCommandRunnerTests and ConfigCommandsEncryptionTests).</summary>
    public static readonly SemaphoreSlim EnvVarGate = new(1, 1);

    public static string CreateTempRoot(string prefix = "ai-raccoon-tests")
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static InfrastructureOptions CreateInfrastructureOptions(string dataRoot, string rid = "osx-arm64") =>
        new() { DataRoot = dataRoot, Rid = rid, Scope = InstallScope.User };

    /// <summary>BundledModel with a null logger and a factory that never opens real connections; the model copy beside the test host makes EnsureAsync return all-present.</summary>
    public static BundledModel CreateBundledModel() => new(NullLogger<BundledModel>.Instance, new NoopHttpClientFactory());

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Returns the p-th percentile (0–1) of the samples.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double quantile)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var sorted = samples.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(quantile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>Cosine similarity between two float vectors.</summary>
    public static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var aSpan = a.Span;
        var bSpan = b.Span;
        double dot = 0;
        for (var i = 0; i < aSpan.Length; i++)
        {
            dot += aSpan[i] * bSpan[i];
        }

        return dot;
    }
}

/// <summary>Recording fake for the propose tier — tool/hosted-service unit tests that must not touch a bank.</summary>
public sealed class FakePromotionQueue : IPromotionQueue
{
    public string? LastProject { get; private set; }
    public IReadOnlyList<QueueCandidate>? LastCandidates { get; private set; }
    public IReadOnlyList<string>? LastPromoteProjects { get; private set; }
    public List<IReadOnlyList<string>> PromoteCalls { get; } = [];
    public int? LastLimit { get; private set; }
    public PromoteOutcome PromoteOutcome { get; set; } = new([], 0, new Dictionary<string, int>());
    public Exception? PromoteError { get; set; }
    public Exception? GetMetaError { get; set; }
    public TimeSpan GetMetaDelay { get; set; }

    public Task<ProposeOutcome> ProposeAsync(string projectId, IReadOnlyList<QueueCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        LastProject = projectId;
        LastCandidates = candidates;
        return Task.FromResult(new ProposeOutcome(candidates.Count, []));
    }

    public Task<PromoteOutcome> PromoteAsync(IReadOnlyList<string> projectIds, int limit,
        CancellationToken cancellationToken = default)
    {
        LastPromoteProjects = projectIds;
        PromoteCalls.Add(projectIds);
        LastLimit = limit;
        if (PromoteError is not null)
        {
            throw PromoteError;
        }

        return Task.FromResult(PromoteOutcome);
    }

    public Task<int> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PromotionQueueRow>>([]);

    public async Task<ResponseMeta> GetMetaAsync(CancellationToken cancellationToken = default)
    {
        if (GetMetaDelay > TimeSpan.Zero)
        {
            await Task.Delay(GetMetaDelay, cancellationToken);
        }

        if (GetMetaError is not null)
        {
            throw GetMetaError;
        }

        return new ResponseMeta(0, null, null);
    }
}
