namespace AiRaccoon.Infrastructure.Embedding;

public interface IBundledModel
{
    /// <summary>
    ///     Verifies both bundled files (sha256) and, when missing, downloads the pinned copies
    ///     into the repo's src/AiRaccoon/Models so the next build packs them. Download failures
    ///     become error entries — a missing asset is a hard failure for the gate test, not a skip.
    /// </summary>
    Task<BundledModelResult> EnsureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Downloads both bundled assets into targetDirectory when no verified copy sits there; download failures become
    ///     error entries (see docs/work/features-native-memory/native-memory.feature).
    /// </summary>
    Task<BundledModelResult> EnsureDownloadsAsync(string targetDirectory, CancellationToken cancellationToken);
}
