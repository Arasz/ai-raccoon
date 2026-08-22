using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption;

namespace AiRaccoon.Tests;

/// <summary>
///     Trait values for filtering: `dotnet test --filter "Category=Unit&Speed=Fast"`.
///     Category: Unit (pure logic / fakes), Integration (real SQLite or native extensions),
///     E2E (full server over HTTP via WebApplicationFactory). Speed: Fast, Slow or Nightly —
///     Nightly is excluded from every push-gate filter and runs only via nightly.yml's
///     unfiltered `dotnet test`.
/// </summary>
public static class TestCategories
{
    public const string Category = "Category";
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string E2E = "E2E";
    public const string Retrieval = "Retrieval";

    public const string Speed = "Speed";
    public const string Fast = "Fast";
    public const string Slow = "Slow";
    public const string Nightly = "Nightly";

    /// <summary>
    ///     Orthogonal to Category/Speed: marks a test whose verdict depends on how fast the host
    ///     is — a wall-clock budget, a latency percentile, a throughput floor. Owner ruling
    ///     (2026-08-22): "the budget is for benchmarking or for special performance regression test
    ///     suite — not for the unit tests", because such a test can go red with no defect present.
    ///     Every PR lane in .github/workflows/build.yml excludes it with
    ///     <c>&amp;Performance!=Benchmark</c>; it still runs in nightly.yml's unfiltered backstop,
    ///     and on demand with <c>dotnet test --filter "Performance=Benchmark"</c>.
    ///     <para />
    ///     Reach for it only when the budget IS the acceptance criterion. A correctness test that
    ///     happens to hold a <c>Stopwatch</c> — a timeout, a refusal, a fast-path-vs-budget-expiry
    ///     choice — belongs on a fake clock instead, not behind this trait.
    /// </summary>
    public const string Performance = "Performance";

    public const string Benchmark = "Benchmark";
}

/// <summary>Never resolves a passphrase — no encryption. Use for existing unencrypted-DB tests.</summary>
public sealed class NullKeyProvider : IEncryptionKeyResolver
{
    public Task<ResolvedKey> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(ResolvedKey.None);

    public static IEncryptionKeyResolver Resolver(InfrastructureOptions options) => new NullKeyProvider();
}
