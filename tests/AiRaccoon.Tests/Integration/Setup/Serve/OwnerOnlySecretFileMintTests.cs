using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     A mint that does not land never leaks the secret it generated: a lost exclusive create and a
///     cancelled write both dispose it, and a landed mint hands it back undisposed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OwnerOnlySecretFileMintTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-secret-mint");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task AMintWhoseWriteIsCancelled_DisposesTheSecretItGenerated()
    {
        var file = NewFile();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => file.TryMintAsync(cancelled.Token));

        file.LastMinted.ShouldNotBeNull().Disposed.ShouldBeTrue();
    }

    [RetryFact]
    public async Task AMintThatLosesTheExclusiveCreate_DisposesTheSecretItGenerated()
    {
        var file = NewFile();
        await File.WriteAllTextAsync(file.Path, "taken", TestContext.Current.CancellationToken);

        (await file.TryMintAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

        file.LastMinted.ShouldNotBeNull().Disposed.ShouldBeTrue();
    }

    [RetryFact]
    public async Task AMintThatLands_ReturnsTheSecretUndisposed()
    {
        var file = NewFile();

        var minted = await file.TryMintAsync(TestContext.Current.CancellationToken);

        minted.ShouldNotBeNull().ShouldBeSameAs(file.LastMinted);
        minted.Disposed.ShouldBeFalse();
    }

    private TrackedSecretFile NewFile()
    {
        var file = new TrackedSecretFile(TestData.CreateProjectOptions(_dataRoot));
        OwnerOnlyFile.EnsureDirectory(file.StateDirectory);
        return file;
    }

    private sealed class TrackedSecret : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class TrackedSecretFile(InfrastructureOptions options)
        : OwnerOnlySecretFile<TrackedSecret>(options, "tracked-secret", null, null)
    {
        private static readonly SemaphoreSlim TrackedGate = new(1, 1);

        public TrackedSecret? LastMinted { get; private set; }

        protected override SemaphoreSlim Gate => TrackedGate;

        protected override TrackedSecret Mint() => LastMinted = new TrackedSecret();

        protected override TrackedSecret? Parse(string content, out string? refusal)
        {
            refusal = null;
            return content.Length > 0 ? new TrackedSecret() : null;
        }

        protected override string Serialize(TrackedSecret secret) => "secret";
    }
}
