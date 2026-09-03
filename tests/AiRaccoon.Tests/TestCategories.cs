using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption;

namespace AiRaccoon.Tests;

/// <summary>
///     Trait values for filtering: `dotnet test --filter "Category=Unit&Speed=Fast"`.
///     Category: Unit (pure logic / fakes), Integration (real SQLite or native extensions),
///     E2E (full server over HTTP via WebApplicationFactory). Speed: Fast, Slow or Nightly —
///     Nightly is excluded from all three required gates (build-fast, build-bdd, build-slow).
///     Its only runner is build-nightly-gates, which is opt-in — the run-nightly-gates label or
///     a manual dispatch — and is not a required check, so a Nightly test can go red on main
///     without any gate seeing it. Run it locally with --filter "Speed=Nightly".
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
    ///     Marks a test whose verdict depends on host speed (a wall-clock budget or percentile). Excluded
    ///     from every PR lane via <c>Performance!=Benchmark</c> (see .github/workflows/build.yml); runs on
    ///     demand with --filter "Performance=Benchmark". A correctness test holding a Stopwatch belongs on
    ///     a fake clock instead.
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
